using System.Data;
using Dapper;

namespace D365FO.Core.Index;

internal sealed class SqliteBoolHandler : SqlMapper.TypeHandler<bool>
{
    public override bool Parse(object value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToInt64(value) != 0,
    };

    public override void SetValue(IDbDataParameter parameter, bool value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = value ? 1L : 0L;
    }
}
