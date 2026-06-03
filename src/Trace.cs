namespace UdonGitFilters
{
    public static class Trace
    {
        private static bool isEnabled;

        public static void SetEnabled(bool isEnabled)
        {
            Trace.isEnabled = isEnabled;
        }

        public static void Info(string msg)
        {
            if (!isEnabled)
                return;
            Console.Error.WriteLine(msg);
            Console.Error.Flush();
        }
    }
}
