#pragma warning disable CS8767
#pragma warning disable CS8765
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RDM.Infrastructure.Database;

namespace RDM.API.Tests;

public class FakeDatabaseBootstrapper : DatabaseBootstrapper
{
    public FakeDatabaseBootstrapper(IConfiguration configuration, MigrationRunner migrationRunner)
        : base(configuration, migrationRunner)
    {
    }

    public override Task<BootstrapResult> RunAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new BootstrapResult(false, null));
    }
}

public class FakeDbConnection : DbConnection
{
    public override string ConnectionString { get; set; } = "";
    public override int ConnectionTimeout => 0;
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "";
    public override ConnectionState State => ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => null!;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    protected override DbCommand CreateDbCommand() => new FakeDbCommand();
    public override void Open() { }
}

public class FakeDbCommand : DbCommand
{
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
    protected override DbTransaction? DbTransaction { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    public override void Cancel() { }
    protected override DbParameter CreateDbParameter() => new FakeParameter();
    
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new FakeDataReader();
    public override int ExecuteNonQuery() => 0;
    public override object ExecuteScalar() => "test-studio-id";
    public override void Prepare() { }
}

public class FakeParameterCollection : DbParameterCollection
{
    private readonly List<object> _list = new();

    public override int Count => _list.Count;
    public override object SyncRoot => ((ICollection)_list).SyncRoot;
    public override int Add(object value) { _list.Add(value); return _list.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v); }
    public override void Clear() => _list.Clear();
    public override bool Contains(object value) => _list.Contains(value);
    public override int IndexOf(object value) => _list.IndexOf(value);
    public override void Insert(int index, object value) => _list.Insert(index, value);
    public override void Remove(object value) => _list.Remove(value);
    public override void RemoveAt(int index) => _list.RemoveAt(index);
    public override IEnumerator GetEnumerator() => _list.GetEnumerator();
    public override void CopyTo(Array array, int index) => ((ICollection)_list).CopyTo(array, index);
    protected override DbParameter GetParameter(int index) => (DbParameter)_list[index];
    protected override DbParameter GetParameter(string parameterName) => throw new NotImplementedException();
    public override void RemoveAt(string parameterName) { }
    public override bool Contains(string value) => false;
    public override int IndexOf(string parameterName) => -1;
    protected override void SetParameter(int index, DbParameter value) => _list[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => throw new NotImplementedException();
}

public class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    public override string ParameterName { get; set; } = "";
    public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() { }
}

public class FakeDataReader : DbDataReader
{
    private bool _read = false;

    public override int Depth => 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int FieldCount => 1;
    public override bool HasRows => true;

    public override object this[int ordinal] => "test-studio-id";
    public override object this[string name] => "test-studio-id";

    public override bool GetBoolean(int ordinal) => false;
    public override byte GetByte(int ordinal) => 0;
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => '\0';
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override string GetDataTypeName(int ordinal) => "string";
    public override DateTime GetDateTime(int ordinal) => DateTime.MinValue;
    public override decimal GetDecimal(int ordinal) => 0;
    public override double GetDouble(int ordinal) => 0;
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override float GetFloat(int ordinal) => 0;
    public override Guid GetGuid(int ordinal) => Guid.Empty;
    public override short GetInt16(int ordinal) => 0;
    public override int GetInt32(int ordinal) => 0;
    public override long GetInt64(int ordinal) => 0;
    public override string GetName(int ordinal) => "studio_id";
    public override int GetOrdinal(string name) => 0;
    public override string GetString(int ordinal) => "test-studio-id";
    public override object GetValue(int ordinal) => "test-studio-id";
    public override int GetValues(object[] values)
    {
        values[0] = "test-studio-id";
        return 1;
    }
    public override bool IsDBNull(int ordinal) => false;
    public override bool NextResult() => false;
    public override bool Read()
    {
        if (!_read)
        {
            _read = true;
            return true;
        }
        return false;
    }
    public override IEnumerator GetEnumerator() => throw new NotImplementedException();
}
