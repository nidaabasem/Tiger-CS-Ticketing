using System.Reflection;
using Microsoft.Data.SqlClient;

namespace TigerCS.Tests.Collections.Crm;

/// <summary>A real <see cref="SqlException"/> (the type has no public constructor) with chosen number, class, state, server and message.</summary>
internal static class SqlExceptionFactory
{
    public static SqlException Create(int number, byte errorClass = 16, byte state = 1, string server = "sql-host", string message = "message", string procedure = "")
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var ctor = typeof(SqlError).GetConstructors(flags).OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p =>
        {
            var name = p.Name!.ToLowerInvariant();
            if (p.ParameterType == typeof(int) && (name.Contains("number") || name == "infonumber")) return (object)number;
            if (p.ParameterType == typeof(byte) && name.Contains("state")) return state;
            if (p.ParameterType == typeof(byte) && name.Contains("class")) return errorClass;
            if (p.ParameterType == typeof(string) && name.Contains("server")) return server;
            if (p.ParameterType == typeof(string) && name.Contains("message")) return message;
            if (p.ParameterType == typeof(string) && name.Contains("proc")) return procedure;
            if (p.ParameterType == typeof(string)) return "";
            return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)! : null!;
        }).ToArray();
        var error = (SqlError)ctor.Invoke(args);
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(collection, [error]);
        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(string));
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
