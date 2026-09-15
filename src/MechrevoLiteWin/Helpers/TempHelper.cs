namespace MechrevoLite.Helpers
{
    public static class TempHelper
    {
        public static string FormatTemp(double celsius)
        {
            return Math.Round(celsius).ToString() + "°C";
        }
    }
}
