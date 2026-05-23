using System;
using System.Collections.Generic;
using System.Web;

namespace Webb
{
    internal static class SseQueryParser
    {
        public static SseEventFilter Parse(string query)
        {
            HashSet<string> include = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> exclude = new(StringComparer.OrdinalIgnoreCase);

            string eventsParam = HttpUtility.ParseQueryString(query)["events"];
            if (string.IsNullOrEmpty(eventsParam))
                return new SseEventFilter(include, exclude);

            foreach (string e in eventsParam.Split(','))
            {
                string trimmed = e.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                if (trimmed.StartsWith("-"))
                    exclude.Add(trimmed.Substring(1));
                else
                    include.Add(trimmed);
            }

            return new SseEventFilter(include, exclude);
        }
    }
}
