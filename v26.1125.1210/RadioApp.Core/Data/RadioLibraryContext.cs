using System;
using System.Data;
using System.Data.Common;
using System.IO;
using Microsoft.EntityFrameworkCore;
using RadioApp.Models;

namespace RadioApp.Data;

public class RadioLibraryContext : DbContext
{
	private readonly string? _databasePathOverride;

	public DbSet<LibraryItem> LibraryItems { get; set; } = null!;

	public DbSet<ScheduleItem> ScheduleItems { get; set; } = null!;

	public DbSet<Playset> Playsets { get; set; } = null!;

	public DbSet<PlaysetEntry> PlaysetEntries { get; set; } = null!;

	public DbSet<PersistedQueueItem> PersistedQueueItems { get; set; } = null!;

	public string DatabasePath => _databasePathOverride ?? GetDefaultDatabasePath();

	public RadioLibraryContext()
	{
	}

	public RadioLibraryContext(string databasePath)
	{
		_databasePathOverride = databasePath;
	}

	public static string GetDefaultAppDataFolder()
	{
		string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioApp");
		Directory.CreateDirectory(folder);
		return folder;
	}

	public static string GetDefaultDatabasePath()
	{
		return Path.Combine(GetDefaultAppDataFolder(), "radioapp.db");
	}

	public void EnsureDatabase()
	{
		Database.EnsureCreated();
		ConfigureSqlitePragmas();

		// LibraryItems backward-compatible schema migrations
		EnsureColumn("LibraryItems", "SourceModifiedAtUtc", "TEXT NULL");
		EnsureColumn("LibraryItems", "LastPlayedAtUtc", "TEXT NULL");
		EnsureColumn("LibraryItems", "PlayCount", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("LibraryItems", "StartCueMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "IntroCueMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "OutroCueMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "EndCueMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "Bpm", "REAL NULL");
		EnsureColumn("LibraryItems", "BpmConfidence", "REAL NULL");
		EnsureColumn("LibraryItems", "FirstBeatMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "OutroBeatMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "LoudnessLufs", "REAL NULL");
		EnsureColumn("LibraryItems", "TruePeakDb", "REAL NULL");
		EnsureColumn("LibraryItems", "AutoCueInMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "AutoMixOutMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "AutoCueOutMs", "INTEGER NULL");
		EnsureColumn("LibraryItems", "IntroProfile", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("LibraryItems", "OutroProfile", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("LibraryItems", "EncoderDelaySamples", "INTEGER NULL");
		EnsureColumn("LibraryItems", "EncoderPaddingSamples", "INTEGER NULL");
		EnsureColumn("LibraryItems", "WaveformPeaksBase64", "TEXT NULL");
		EnsureColumn("LibraryItems", "AnalysisVersion", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("LibraryItems", "FileExistsCached", "INTEGER NOT NULL DEFAULT 1");

		// ScheduleItems backward-compatible schema migrations
		EnsureColumn("ScheduleItems", "RepeatMode", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "ToleranceBeforeMinutes", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "ToleranceAfterMinutes", "INTEGER NOT NULL DEFAULT 2");
		EnsureColumn("ScheduleItems", "IsMain", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "IsFolderSource", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("ScheduleItems", "BlockDurationMinutes", "INTEGER NOT NULL DEFAULT 60");
		EnsureColumn("ScheduleItems", "PlaysetId", "INTEGER NULL");

		// New v26.1120 tables (created safely if upgrading an older database)
		ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS Playsets (
	Id INTEGER NOT NULL CONSTRAINT PK_Playsets PRIMARY KEY AUTOINCREMENT,
	Name TEXT NOT NULL DEFAULT '',
	Description TEXT NOT NULL DEFAULT '',
	Kind INTEGER NOT NULL DEFAULT 0,
	AccentColor TEXT NOT NULL DEFAULT '#0284C7',
	TransitionModeOverride INTEGER NULL,
	RuleJson TEXT NOT NULL DEFAULT '',
	CreatedAtUtc TEXT NOT NULL DEFAULT '',
	UpdatedAtUtc TEXT NOT NULL DEFAULT ''
);");

		ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS PlaysetEntries (
	Id INTEGER NOT NULL CONSTRAINT PK_PlaysetEntries PRIMARY KEY AUTOINCREMENT,
	PlaysetId INTEGER NOT NULL,
	LibraryItemId INTEGER NOT NULL,
	SortOrder INTEGER NOT NULL DEFAULT 0,
	CustomTransitionMs INTEGER NULL,
	Notes TEXT NOT NULL DEFAULT '',
	CONSTRAINT FK_PlaysetEntries_Playsets_PlaysetId FOREIGN KEY (PlaysetId) REFERENCES Playsets (Id) ON DELETE CASCADE,
	CONSTRAINT FK_PlaysetEntries_LibraryItems_LibraryItemId FOREIGN KEY (LibraryItemId) REFERENCES LibraryItems (Id) ON DELETE CASCADE
);");

		ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS PersistedQueueItems (
	Id INTEGER NOT NULL CONSTRAINT PK_PersistedQueueItems PRIMARY KEY AUTOINCREMENT,
	SortOrder INTEGER NOT NULL DEFAULT 0,
	LibraryItemId INTEGER NULL,
	FilePath TEXT NOT NULL DEFAULT '',
	Title TEXT NOT NULL DEFAULT '',
	Artist TEXT NOT NULL DEFAULT '',
	Album TEXT NOT NULL DEFAULT '',
	Category TEXT NOT NULL DEFAULT 'Music',
	DurationTicks INTEGER NOT NULL DEFAULT 0,
	IsAutoDj INTEGER NOT NULL DEFAULT 0,
	SourcePlaysetId INTEGER NULL
);");

		ExecuteNonQuery("CREATE INDEX IF NOT EXISTS IX_LibraryItems_FilePath ON LibraryItems (FilePath);");
		ExecuteNonQuery("CREATE INDEX IF NOT EXISTS IX_LibraryItems_Category ON LibraryItems (Category);");
		ExecuteNonQuery("CREATE INDEX IF NOT EXISTS IX_PlaysetEntries_PlaysetId_SortOrder ON PlaysetEntries (PlaysetId, SortOrder);");
	}

	public string CreateBackup(string? destinationPath = null)
	{
		string backupDir = Path.Combine(GetDefaultAppDataFolder(), "backups");
		Directory.CreateDirectory(backupDir);
		string target = string.IsNullOrWhiteSpace(destinationPath)
			? Path.Combine(backupDir, $"radioapp-{DateTime.Now:yyyyMMdd-HHmmss}.db")
			: destinationPath;

		// Flush WAL before copying
		ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
		File.Copy(DatabasePath, target, overwrite: true);
		return target;
	}

	public (bool Ok, string Message) VerifyIntegrity()
	{
		try
		{
			DbConnection connection = Database.GetDbConnection();
			bool shouldClose = connection.State == ConnectionState.Closed;
			if (shouldClose) connection.Open();
			try
			{
				using DbCommand cmd = connection.CreateCommand();
				cmd.CommandText = "PRAGMA integrity_check;";
				string result = Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
				bool ok = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
				return (ok, ok ? "SQLite integrity check passed." : $"Integrity check warning: {result}");
			}
			finally
			{
				if (shouldClose) connection.Close();
			}
		}
		catch (Exception ex)
		{
			return (false, $"Integrity check failed: {ex.Message}");
		}
	}

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
	{
		string databasePath = DatabasePath;
		string? dir = Path.GetDirectoryName(databasePath);
		if (!string.IsNullOrWhiteSpace(dir))
		{
			Directory.CreateDirectory(dir);
		}
		if (_databasePathOverride == null)
		{
			MigrateLocalDatabase(databasePath);
		}
		optionsBuilder.UseSqlite("Data Source=" + databasePath + ";Cache=Shared");
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
		modelBuilder.Entity<LibraryItem>().HasKey(i => i.Id);
		modelBuilder.Entity<ScheduleItem>().HasKey(s => s.Id);
		modelBuilder.Entity<Playset>().HasKey(p => p.Id);
		modelBuilder.Entity<PlaysetEntry>().HasKey(e => e.Id);
		modelBuilder.Entity<PlaysetEntry>()
			.HasOne(e => e.Playset)
			.WithMany(p => p.Entries)
			.HasForeignKey(e => e.PlaysetId)
			.OnDelete(DeleteBehavior.Cascade);
		modelBuilder.Entity<PlaysetEntry>()
			.HasOne(e => e.LibraryItem)
			.WithMany()
			.HasForeignKey(e => e.LibraryItemId)
			.OnDelete(DeleteBehavior.Cascade);
		modelBuilder.Entity<PersistedQueueItem>().HasKey(q => q.Id);
		base.OnModelCreating(modelBuilder);
	}

	private void ConfigureSqlitePragmas()
	{
		ExecuteNonQuery("PRAGMA journal_mode=WAL;");
		ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
		ExecuteNonQuery("PRAGMA busy_timeout=5000;");
		ExecuteNonQuery("PRAGMA foreign_keys=ON;");
	}

	private void ExecuteNonQuery(string sql)
	{
		DbConnection connection = Database.GetDbConnection();
		bool shouldClose = connection.State == ConnectionState.Closed;
		if (shouldClose) connection.Open();
		try
		{
			using DbCommand cmd = connection.CreateCommand();
			cmd.CommandText = sql;
			cmd.ExecuteNonQuery();
		}
		finally
		{
			if (shouldClose) connection.Close();
		}
	}

	private void EnsureColumn(string tableName, string columnName, string definition)
	{
		DbConnection connection = Database.GetDbConnection();
		bool shouldClose = connection.State == ConnectionState.Closed;
		if (shouldClose) connection.Open();
		try
		{
			using DbCommand checkCommand = connection.CreateCommand();
			checkCommand.CommandText = $"PRAGMA table_info({tableName});";
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
			if (shouldClose) connection.Close();
		}
	}
}

public class RadioDbContextFactory
{
	private readonly string? _databasePathOverride;
	private bool _initialized;
	private readonly object _initGate = new object();

	public RadioDbContextFactory(string? databasePathOverride = null)
	{
		_databasePathOverride = databasePathOverride;
	}

	public RadioLibraryContext CreateContext()
	{
		RadioLibraryContext ctx = _databasePathOverride == null
			? new RadioLibraryContext()
			: new RadioLibraryContext(_databasePathOverride);

		if (!_initialized)
		{
			lock (_initGate)
			{
				if (!_initialized)
				{
					ctx.EnsureDatabase();
					_initialized = true;
				}
			}
		}
		return ctx;
	}
}
