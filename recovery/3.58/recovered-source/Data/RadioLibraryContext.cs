using System;
using System.Data;
using System.Data.Common;
using System.IO;
using Microsoft.EntityFrameworkCore;
using RadioApp.Models;

namespace RadioApp.Data;

public class RadioLibraryContext : DbContext
{
	public DbSet<LibraryItem> LibraryItems { get; set; }

	public DbSet<ScheduleItem> ScheduleItems { get; set; }

	public void EnsureDatabase()
	{
		Database.EnsureCreated();
		EnsureLibraryColumn("SourceModifiedAtUtc", "TEXT NULL");
		EnsureLibraryColumn("LastPlayedAtUtc", "TEXT NULL");
		EnsureLibraryColumn("PlayCount", "INTEGER NOT NULL DEFAULT 0");
		EnsureLibraryColumn("StartCueMs", "INTEGER NULL");
		EnsureLibraryColumn("IntroCueMs", "INTEGER NULL");
		EnsureLibraryColumn("OutroCueMs", "INTEGER NULL");
		EnsureLibraryColumn("EndCueMs", "INTEGER NULL");
		EnsureScheduleColumn("RepeatMode", "INTEGER NOT NULL DEFAULT 0");
		EnsureScheduleColumn("ToleranceBeforeMinutes", "INTEGER NOT NULL DEFAULT 0");
		EnsureScheduleColumn("ToleranceAfterMinutes", "INTEGER NOT NULL DEFAULT 2");
		EnsureScheduleColumn("IsMain", "INTEGER NOT NULL DEFAULT 0");
		EnsureScheduleColumn("IsFolderSource", "INTEGER NOT NULL DEFAULT 0");
		EnsureScheduleColumn("BlockDurationMinutes", "INTEGER NOT NULL DEFAULT 60");
	}

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioApp");
		Directory.CreateDirectory(text);
		string databasePath = Path.Combine(text, "radioapp.db");
		MigrateLocalDatabase(databasePath);
		optionsBuilder.UseSqlite("Data Source=" + databasePath);
	}

	private static void MigrateLocalDatabase(string databasePath)
	{
		if (!File.Exists(databasePath))
		{
			string localDatabasePath = Path.Combine(AppContext.BaseDirectory, "radioapp.db");
			if (File.Exists(localDatabasePath))
			{
				File.Copy(localDatabasePath, databasePath);
			}
		}
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<LibraryItem>().HasKey((LibraryItem i) => i.Id);
		modelBuilder.Entity<ScheduleItem>().HasKey((ScheduleItem s) => s.Id);
		base.OnModelCreating(modelBuilder);
	}

	private void EnsureScheduleColumn(string columnName, string definition)
	{
		EnsureColumn("ScheduleItems", columnName, definition);
	}

	private void EnsureLibraryColumn(string columnName, string definition)
	{
		EnsureColumn("LibraryItems", columnName, definition);
	}

	private void EnsureColumn(string tableName, string columnName, string definition)
	{
		DbConnection connection = Database.GetDbConnection();
		bool shouldClose = connection.State == ConnectionState.Closed;
		if (shouldClose)
		{
			connection.Open();
		}
		try
		{
			using DbCommand checkCommand = connection.CreateCommand();
			checkCommand.CommandText = "PRAGMA table_info(" + tableName + ");";
			using DbDataReader reader = checkCommand.ExecuteReader();
			while (reader.Read())
			{
				if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}
			using DbCommand alterCommand = connection.CreateCommand();
			alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
			alterCommand.ExecuteNonQuery();
		}
		finally
		{
			if (shouldClose)
			{
				connection.Close();
			}
		}
	}
}
