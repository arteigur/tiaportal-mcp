using Siemens.Engineering;
using System;
using System.Collections.Generic;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Turns an exception thrown by an Openness import into one message for the caller.
    ///
    /// TIA Portal names the exact problem in the exception (and often in an inner exception), for
    /// example which part of a network is out of order. The import tools used to swallow it and
    /// only report "failed". An EngineeringException also carries TIA Portal's detail messages
    /// (for a source that does not compile: the line and what is wrong with it), which are often
    /// not part of its Message, so they are added too. Callers: the import tools in Portal and
    /// Operation.Run, which decorates every failed Portal operation. Reads/writes no data files.
    /// </summary>
    public static class ImportErrors
    {
        private const string FlowOrderMarker = "must be sorted according to the current flow";

        private const string FlowOrderHint =
            "TIA Portal needs the elements of a network in flow order: put each new <Part> directly " +
            "after the part that feeds it, the order TIA Portal's own export uses.";

        /// <summary>
        /// The distinct messages of the exception and its inner exceptions, outermost first,
        /// joined with ': '. A hint is appended for failures that have a known remedy.
        /// </summary>
        public static string Describe(Exception exception)
        {
            var messages = new List<string>();

            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                Add(messages, current.Message);

                if (current is EngineeringException engineering)
                {
                    foreach (var detail in Details(engineering))
                    {
                        Add(messages, detail);
                    }
                }
            }

            var text = string.Join(": ", messages);

            if (text.IndexOf(FlowOrderMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                text += " " + FlowOrderHint;
            }

            return text;
        }

        private static void Add(List<string> messages, string? message)
        {
            message = message?.Trim();

            if (!string.IsNullOrEmpty(message) && !messages.Contains(message!))
            {
                messages.Add(message!);
            }
        }

        private static IEnumerable<string> Details(EngineeringException exception)
        {
            var details = new List<string>();

            try
            {
                Add(details, Text(exception.MessageData));

                foreach (var data in exception.DetailMessageData ?? Array.Empty<ExceptionMessageData>())
                {
                    Add(details, Text(data));
                }
            }
            catch (Exception)
            {
                // The message data is a nice-to-have; never lose the original error over it.
            }

            return details;
        }

        private static string? Text(ExceptionMessageData data)
        {
            var text = data.Text?.Trim();
            var detail = data.DetailText?.Trim();

            if (string.IsNullOrEmpty(detail) || string.Equals(text, detail, StringComparison.Ordinal))
            {
                return text;
            }

            return string.IsNullOrEmpty(text) ? detail : $"{text} ({detail})";
        }
    }
}
