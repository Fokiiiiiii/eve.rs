namespace Eve.Events.Tests;

public sealed class DomainEventTests
{
    [Fact]
    public void Json_returns_a_clone_that_remains_readable_after_the_document_is_disposed()
    {
        var domainEvent = new DomainEvent(1, "test", null, "{\"nested\":{\"values\":[1,2,3]}}", 0);

        var json = domainEvent.Json();

        Assert.Equal(2, json.GetProperty("nested").GetProperty("values")[1].GetInt32());
    }
}
