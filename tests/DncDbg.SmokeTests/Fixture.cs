partial class Program
{
    public static void ExpressionFixture()
    {
        string text = "test";
        int value = 5;
        decimal raw = 10.5m;
        int converted = (int)raw;
        decimal rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        var sharedAddress = new ExpressionAddress();
        var first = new ExpressionInvoice(7, sharedAddress);
        var second = new ExpressionInvoice(42, sharedAddress);
        bool sameReference = object.ReferenceEquals(first.Address, second.Address);
        bool sameOperator = first.Address == second.Address;
        List<int> readings = [2, 7, 9];
        IEnumerable<int> selected = readings.Where(item => item >= 5);
        string joined = string.Join(",", selected);
        int count = selected.Count();
        List<int> clearedReadings = [2, 7, 9];
        IEnumerable<int> selectedAfterClear = clearedReadings.Where(item => item >= 5);
        clearedReadings.Clear();
        string joinedAfterClear = string.Join(",", selectedAfterClear);
        int countAfterClear = selectedAfterClear.Count();
        var record = new ExpressionRecord(42, 105m, 10m);
        var authored = new AuthoredEqualityContract();

        System.Diagnostics.Debugger.Break();
        GC.KeepAlive(new object[]
        {
            text, value, raw, converted, rounded, first, second, sameReference, sameOperator, selected, joined, count,
            selectedAfterClear, joinedAfterClear, countAfterClear, record, authored,
        });
    }

    public static int WaitDuringEval()
    {
        System.Diagnostics.Debugger.Log(0, "", "evaluation-entered");
        for (int i = 0; i < 100; i++)
            Thread.Sleep(100);
        return 1;
    }

    private sealed class ExpressionAddress
    {
        public string City { get; set; } = "east";
    }

    private sealed record ExpressionInvoice(int Id, ExpressionAddress Address);
    private sealed record ExpressionRecord(int Id, decimal Subtotal, decimal DiscountRate);

    private sealed class AuthoredEqualityContract
    {
        public int Id => 7;
        public string EqualityContract => "authored";
    }

    internal static class DeferredInitializer
    {
        private static readonly int Value;

        static DeferredInitializer()
        {
            Value = 42;
        }

        public static int Read() => Value;
    }
}
