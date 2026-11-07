using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HS2Manager.Core;

public sealed class CardRepository
{
    private readonly string connectionString;
    private readonly object gate = new();
    public string DataPath { get; }
    public CardRepository(string dataPath)
    {
        DataPath = System.IO.Path.GetFullPath(dataPath); Directory.CreateDirectory(DataPath);
        connectionString = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(DataPath, "library.db"), Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Cards(Id TEXT PRIMARY KEY, Path TEXT NOT NULL COLLATE NOCASE UNIQUE, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Metadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public List<CardRecord> All()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Json FROM Cards";
            using var reader = cmd.ExecuteReader(); var cards = new List<CardRecord>();
            while (reader.Read()) { var record = JsonSerializer.Deserialize<CardRecord>(reader.GetString(0)); if (record != null) cards.Add(record); } return cards;
        }
    }
    public CardRecord? FindByPath(string path)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Json FROM Cards WHERE Path=$path"; cmd.Parameters.AddWithValue("$path", System.IO.Path.GetFullPath(path));
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CardRecord>(json) : null;
        }
    }
    public CardRecord? FindById(string id)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Json FROM Cards WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CardRecord>(json) : null;
        }
    }
    public void Save(CardRecord card)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO Cards(Id,Path,Json) VALUES($id,$path,$json) ON CONFLICT(Id) DO UPDATE SET Path=excluded.Path,Json=excluded.Json";
            cmd.Parameters.AddWithValue("$id", card.Id); cmd.Parameters.AddWithValue("$path", card.Path); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(card)); cmd.ExecuteNonQuery();
        }
    }
    public CardRecord SaveScan(CardRecord parsed)
    {
        lock (gate)
        {
            var old = FindByPath(parsed.Path);
            if (old != null)
            {
                parsed.Id = old.Id; parsed.Favorite = old.Favorite; parsed.Tags = old.Tags; parsed.Collection = old.Collection; parsed.Notes = old.Notes;
                if (old.Size == parsed.Size && old.ModifiedUtcTicks == parsed.ModifiedUtcTicks) parsed.Hash = old.Hash;
            }
            Save(parsed); return parsed;
        }
    }
    public void UpdateMetadata(IEnumerable<string> ids, Action<CardRecord> update)
    {
        lock (gate) foreach (string id in ids) { var card = FindById(id); if (card != null) { update(card); Save(card); } }
    }
    public void Remove(string id)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM Cards WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    }
    public void StoreMetadata<T>(string key, T value)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO Metadata(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value)); cmd.ExecuteNonQuery();
        }
    }
    public T? LoadMetadata<T>(string key)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Value FROM Metadata WHERE Key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default; }
    }
    public void Backup(string target)
    {
        lock (gate) { using var source = Open(); using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString()); destination.Open(); source.BackupDatabase(destination); }
    }
}
