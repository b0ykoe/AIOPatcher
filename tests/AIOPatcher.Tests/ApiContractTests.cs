using System.Text.Json;
using AIOPatcher.Server.Endpoints;

namespace AIOPatcher.Tests;

public sealed class ApiContractTests
{
    public static IEnumerable<object[]> DtoTypes() =>
        typeof(AuthEndpoints).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(AuthEndpoints).Namespace && t.IsNested && t.IsClass && !t.IsAbstract && t.GetMethod("<Clone>$") is not null)
            .Select(t => new object[] { t });

    /// <summary>
    /// ASP.NET serialisiert mit Web-Defaults (camelCase, ohne Groß-/Kleinschreibung): "HostName" und "Hostname"
    /// kollidieren erst beim Start des Servers. Der Test fängt das vorher ab.
    /// </summary>
    [Theory]
    [MemberData(nameof(DtoTypes))]
    public void Dto_HasNoJsonNameCollisions(Type type)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        var info = options.GetTypeInfo(type);
        Assert.NotNull(info);
    }

    [Fact]
    public void FindsDtos() => Assert.True(DtoTypes().Count() > 30);
}
