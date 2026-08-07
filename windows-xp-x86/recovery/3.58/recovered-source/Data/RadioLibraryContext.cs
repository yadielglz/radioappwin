using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using RadioApp.Models;

namespace RadioApp.Data;

public sealed class TrackedTable<T> : List<T>
{
	public void RemoveRange(IEnumerable<T> items)
	{
		foreach (T item in items.ToList())
		{
			Remove(item);
		}
	}
}

public sealed class RadioLibraryContext : IDisposable
{
	private readonly SQLiteConnection _connection;

	public TrackedTable<LibraryItem> LibraryItems { get; private set; }
	public TrackedTable<ScheduleItem> ScheduleItems { get; private set; }

	public RadioLibraryContext()
	{
		string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioApp");
		Directory.CreateDirectory(folder);
		string databasePath = Path.Combine(folder, "radioapp.db");
		MigrateLocalDatabase(databasePath);
		_connection = new SQLiteConnection("Data Source=" + databasePath + ";Version=3;");
		_connection.Open();
		LibraryItems = new TrackedTable<LibraryItem>();
		ScheduleItems = new TrackedTable<ScheduleItem>();
	}

	public void EnsureDatabase()
	{
		ExecuteNonQuery("CREATE TABLE IF NOT EXISTS LibraryItems (Id INTEGER PRIMARY KEY AUTOINCREMENT, FilePath TEXT NOT NULL, Title TEXT NOT NULL, Artist TEXT NOT NULL, Album TEXT NOT NULL, Category TEXT NOT NULL, Tags TEXT NOT NULL, Duration INTEGER NOT NULL, DateAdded TEXT NOT NULL, SourceModifiedAtUtc TEXT NULL)");
		ExecuteNonQuery("CREATE TABLE IF NOT EXISTS ScheduleItems (Id INTEGER PRIMARY KEY AUTOINCREMENT, Title TEXT NOT NULL, EventType INTEGER NOT NULL, FilePath TEXT NOT NULL, ScheduledAt TEXT NOT NULL, RepeatInterval INTEGER NULL, RepeatMode INTEGER NOT NULL DEFAULT 0, ToleranceBeforeMinutes INTEGER NOT NULL DEFAULT 0, ToleranceAfterMinutes INTEGER NOT NULL DEFAULT 2, IsMain INTEGER NOT NULL DEFAULT 0, Enabled INTEGER NOT NULL DEFAULT 1, TargetDeck INTEGER NOT NULL DEFAULT 0, IsFolderSource INTEGER NOT NULL DEFAULT 0, BlockDurationMinutes INTEGER NOT NULL DEFAULT 60)");
		EnsureColumn("LibraryItems", "SourceModifiedAtUtc", "TEXT NULL");
		EnsureColumn("ScheduleItems", "RepeatMode", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "ToleranceBeforeMinutes", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "ToleranceAfterMinutes", "INTEGER NOT NULL DEFAULT 2");
		EnsureColumn("ScheduleItems", "IsMain", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "IsFolderSource", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "BlockDurationMinutes", "INTEGER NOT NULL DEFAULT 60");
		LoadAll();
	}

	private void LoadAll()
	{
		LibraryItems.Clear();
		using (SQLiteCommand command = new SQLiteCommand("SELECT Id, FilePath, Title, Artist, Album, Category, Tags, Duration, DateAdded, SourceModifiedAtUtc FROM LibraryItems", _connection))
		using (SQLiteDataReader reader = command.ExecuteReader())
		{
			while (reader.Read())
			{
				LibraryItems.Add(new LibraryItem {
					Id = reader.GetInt32(0), FilePath = reader.GetString(1), Title = reader.GetString(2), Artist = reader.GetString(3), Album = reader.GetString(4), Category = reader.GetString(5), Tags = reader.GetString(6),
					Duration = TimeSpan.FromTicks(reader.GetInt64(7)), DateAdded = ParseDate(reader.GetString(8)), SourceModifiedAtUtc = reader.IsDBNull(9) ? (DateTime?)null : ParseDate(reader.GetString(9))
				});
			}
		}
		ScheduleItems.Clear();
		using (SQLiteCommand command = new SQLiteCommand("SELECT Id, Title, EventType, FilePath, ScheduledAt, RepeatInterval, RepeatMode, ToleranceBeforeMinutes, ToleranceAfterMinutes, IsMain, Enabled, TargetDeck, IsFolderSource, BlockDurationMinutes FROM ScheduleItems", _connection))
		using (SQLiteDataReader reader = command.ExecuteReader())
		{
			while (reader.Read())
			{
				ScheduleItems.Add(new ScheduleItem {
					Id = reader.GetInt32(0), Title = reader.GetString(1), EventType = (ScheduleEventType)reader.GetInt32(2), FilePath = reader.GetString(3), ScheduledAt = ParseDate(reader.GetString(4)),
					RepeatInterval = reader.IsDBNull(5) ? (TimeSpan?)null : TimeSpan.FromTicks(reader.GetInt64(5)), RepeatMode = (ScheduleRepeatMode)reader.GetInt32(6), ToleranceBeforeMinutes = reader.GetInt32(7), ToleranceAfterMinutes = reader.GetInt32(8), IsMain = reader.GetBoolean(9), Enabled = reader.GetBoolean(10), TargetDeck = (DeckId)reader.GetInt32(11), IsFolderSource = reader.GetBoolean(12), BlockDurationMinutes = reader.GetInt32(13)
				});
			}
		}
	}

	public void SaveChanges()
	{
		using (SQLiteTransaction transaction = _connection.BeginTransaction())
		{
			ExecuteNonQuery("DELETE FROM LibraryItems", transaction);
			foreach (LibraryItem item in LibraryItems)
			{
				using (SQLiteCommand command = new SQLiteCommand("INSERT INTO LibraryItems (Id, FilePath, Title, Artist, Album, Category, Tags, Duration, DateAdded, SourceModifiedAtUtc) VALUES (@Id,@FilePath,@Title,@Artist,@Album,@Category,@Tags,@Duration,@DateAdded,@SourceModifiedAtUtc)", _connection, transaction))
				{
					int id = item.Id > 0 ? item.Id : NextId(LibraryItems.Select(x => x.Id)); item.Id = id;
					command.Parameters.AddWithValue("@Id", id); command.Parameters.AddWithValue("@FilePath", item.FilePath); command.Parameters.AddWithValue("@Title", item.Title); command.Parameters.AddWithValue("@Artist", item.Artist); command.Parameters.AddWithValue("@Album", item.Album); command.Parameters.AddWithValue("@Category", item.Category); command.Parameters.AddWithValue("@Tags", item.Tags); command.Parameters.AddWithValue("@Duration", item.Duration.Ticks); command.Parameters.AddWithValue("@DateAdded", FormatDate(item.DateAdded)); command.Parameters.AddWithValue("@SourceModifiedAtUtc", item.SourceModifiedAtUtc.HasValue ? (object)FormatDate(item.SourceModifiedAtUtc.Value) : DBNull.Value); command.ExecuteNonQuery();
				}
			}
			ExecuteNonQuery("DELETE FROM ScheduleItems", transaction);
			foreach (ScheduleItem item in ScheduleItems)
			{
				using (SQLiteCommand command = new SQLiteCommand("INSERT INTO ScheduleItems (Id, Title, EventType, FilePath, ScheduledAt, RepeatInterval, RepeatMode, ToleranceBeforeMinutes, ToleranceAfterMinutes, IsMain, Enabled, TargetDeck, IsFolderSource, BlockDurationMinutes) VALUES (@Id,@Title,@EventType,@FilePath,@ScheduledAt,@RepeatInterval,@RepeatMode,@ToleranceBeforeMinutes,@ToleranceAfterMinutes,@IsMain,@Enabled,@TargetDeck,@IsFolderSource,@BlockDurationMinutes)", _connection, transaction))
				{
					int id = item.Id > 0 ? item.Id : NextId(ScheduleItems.Select(x => x.Id)); item.Id = id;
					command.Parameters.AddWithValue("@Id", id); command.Parameters.AddWithValue("@Title", item.Title); command.Parameters.AddWithValue("@EventType", (int)item.EventType); command.Parameters.AddWithValue("@FilePath", item.FilePath); command.Parameters.AddWithValue("@ScheduledAt", FormatDate(item.ScheduledAt)); command.Parameters.AddWithValue("@RepeatInterval", item.RepeatInterval.HasValue ? (object)item.RepeatInterval.Value.Ticks : DBNull.Value); command.Parameters.AddWithValue("@RepeatMode", (int)item.RepeatMode); command.Parameters.AddWithValue("@ToleranceBeforeMinutes", item.ToleranceBeforeMinutes); command.Parameters.AddWithValue("@ToleranceAfterMinutes", item.ToleranceAfterMinutes); command.Parameters.AddWithValue("@IsMain", item.IsMain); command.Parameters.AddWithValue("@Enabled", item.Enabled); command.Parameters.AddWithValue("@TargetDeck", (int)item.TargetDeck); command.Parameters.AddWithValue("@IsFolderSource", item.IsFolderSource); command.Parameters.AddWithValue("@BlockDurationMinutes", item.BlockDurationMinutes); command.ExecuteNonQuery();
				}
			}
			transaction.Commit();
		}
	}

	private static int NextId(IEnumerable<int> ids) { return ids.DefaultIfEmpty(0).Max() + 1; }
	private static string FormatDate(DateTime value) { return value.ToUniversalTime().ToString("o"); }
	private static DateTime ParseDate(string value) { return DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind); }
	private void ExecuteNonQuery(string sql, SQLiteTransaction transaction = null) { using (SQLiteCommand command = new SQLiteCommand(sql, _connection, transaction)) { command.ExecuteNonQuery(); } }
	private void EnsureColumn(string table, string column, string definition) { using (SQLiteCommand command = new SQLiteCommand("PRAGMA table_info(" + table + ")", _connection)) using (SQLiteDataReader reader = command.ExecuteReader()) { while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return; } ExecuteNonQuery("ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition); }
	private static void MigrateLocalDatabase(string path) { if (!File.Exists(path)) { string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "radioapp.db"); if (File.Exists(local)) File.Copy(local, path); } }
	public void Dispose() { _connection.Dispose(); }
}
