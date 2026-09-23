namespace Dispatchly.Core.Tests;

public class IdentifierRulesTests
{
    [Fact]
    public void FromClrName_ConvertsPascalCase()
    {
        Assert.Equal("order_created", IdentifierRules.FromClrName("OrderCreated"));
    }

    [Fact]
    public void ToSnakeCase_SplitsAcronymBeforeLowercase()
    {
        Assert.Equal("http_response", IdentifierRules.ToSnakeCase("HTTPResponse"));
    }

    [Fact]
    public void ValidateTable_RejectsReservedName()
    {
        var exception = Assert.Throws<ArgumentException>(() => IdentifierRules.ValidateTable("outbox_table"));
        Assert.Contains("reserved", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromClrName_AppendsHashWhenTheNameIsTooLong()
    {
        var name = new string('A', 80);
        var table = IdentifierRules.FromClrName(name);
        Assert.True(table.Length <= IdentifierRules.MaxTableNameLength);
        Assert.Matches("^[a-z_][a-z0-9_]*$", table);
    }

    [Fact]
    public void FromType_UsesDispatchlyTableAttribute()
    {
        Assert.Equal("placed_orders", MessageTableNames.FromType(typeof(PlacedOrder)));
    }

    [DispatchlyTable("placed_orders")]
    private sealed class PlacedOrder;
}
