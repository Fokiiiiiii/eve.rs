using Eve.Marshal;
using Eve.Market;
using Eve.Services.Market;

namespace Eve.Market.Tests;

public sealed class MarketStructureWireTests
{
    [Fact]
    public void Order_rows_preserve_the_64_bit_structure_location_and_describe_it_as_a_long()
    {
        const long structureId=9_000_000_001_001;
        var location=new MarketLocation(structureId,30_000_001,20_000_001,10_000_001);
        var order=new MarketOrder(7,34,false,10m,2,2,1,MarketRules.RangeStation,location,123,90);
        var response=Assert.IsType<PyObject>(MarketWire.Orders([order],new MarketTopology([]),location));
        var fields=Assert.IsType<PyDict>(response.Args);
        PyValue Field(string name)=>fields.Entries.Single(entry=>entry.Key is PyStr key && key.Value==name).Value;

        var descriptor=Assert.IsType<PyObjectEx>(Field("header"));
        var descriptorHeader=Assert.IsType<PyTuple>(descriptor.Header);
        var nestedHeader=Assert.IsType<PyTuple>(descriptorHeader.Items[1]);
        var columns=Assert.IsType<PyTuple>(nestedHeader.Items[0]);
        var stationColumn=columns.Items.Select(value=>Assert.IsType<PyTuple>(value))
            .Single(column=>Assert.IsType<PyStr>(column.Items[0]).Value=="stationID");
        Assert.Equal(new PyInt(20),stationColumn.Items[1]);

        var lines=Assert.IsType<PyList>(Field("lines"));
        var row=Assert.IsType<PyTuple>(Assert.Single(lines.Items));
        Assert.Equal(new PyLong(structureId),row.Items[10]);
    }

    [Fact]
    public void Region_best_order_summaries_preserve_structure_ids_as_longs_in_the_keyval_shape()
    {
        const long structureId=9_000_000_001_001;
        var order=new MarketOrder(7,34,false,10m,2,2,1,MarketRules.RangeStation,
            new MarketLocation(structureId,30_000_001,20_000_001,10_000_001),123,90);
        var summary=Assert.IsType<PyObject>(MarketWire.BestByOrder(order,2));
        var fields=Assert.IsType<PyDict>(summary.Args);
        PyValue Field(string name)=>fields.Entries.Single(entry=>entry.Key is PyStr key && key.Value==name).Value;

        Assert.Equal(new PyInt(34),Field("typeID"));
        Assert.Equal(new PyLong(structureId),Field("stationID"));
    }

    [Fact]
    public void Station_and_system_ask_rows_support_named_fields_and_sequence_unpacking_on_the_wire()
    {
        const long structureId=9_000_000_001_001;
        var order=new MarketOrder(7,34,false,10m,2,2,1,MarketRules.RangeStation,
            new MarketLocation(structureId,30_000_001,20_000_001,10_000_001),123,90);

        void AssertRow(PyObject row)
        {
            Assert.Equal("util.Row",Eve.Macho.JsSemantics.PlainText(row.Name));
            var fields=Assert.IsType<PyDict>(row.Args);
            PyValue Field(string name)=>fields.Entries.Single(entry=>Eve.Macho.JsSemantics.PlainText(entry.Key)==name).Value;
            var header=Assert.IsType<PyTuple>(Field("header"));
            Assert.Equal(new[]{"price","volRemaining","typeID","stationID"},
                header.Items.Select(Eve.Macho.JsSemantics.PlainText));
            var line=Assert.IsType<PyTuple>(Field("line"));
            Assert.Equal(4,line.Items.Count);
            Assert.Equal(10,Number(line.Items[0]));
            Assert.Equal(2,Number(line.Items[1]));
            Assert.Equal(34,Number(line.Items[2]));
            Assert.Equal(structureId,Number(line.Items[3]));
        }

        var summary=Assert.IsType<PyObject>(MarketWire.BestByOrderRow(order,2));
        AssertRow(summary);
        AssertRow(Assert.IsType<PyObject>(MarshalCodec.Decode(MarshalCodec.Encode(summary))));
    }

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => (long)number.Value,
        _ => throw new InvalidOperationException($"expected a number, got {value.GetType().Name}"),
    };

}
