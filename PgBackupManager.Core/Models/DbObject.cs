namespace PgBackupManager.Core.Models;

public enum DbObjectKind { Schema, Table, View, Function, Type, Sequence, Procedure }

public sealed record DbObject(string Schema, string Name, DbObjectKind Kind, string? Signature = null)
{
    public string FullName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    public string DisplayName => string.IsNullOrEmpty(Signature) ? Name : $"{Name}({Signature})";
}
