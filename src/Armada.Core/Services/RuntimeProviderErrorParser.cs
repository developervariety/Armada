namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>Extracts compact provider evidence from explicit transport error cues.</summary>
    public static class RuntimeProviderErrorParser
    {
        private static readonly Regex _StatusCue = new Regex(
            @"(?:\bAPI Error:\s*(?:HTTP\s+)?|\bHTTP(?:/\d(?:\.\d)?)?\s+|\bstatus(?:[_ ]code)?(?:\s*[:=]\s*|\s+)|\bresponse\s+status(?:\s*[:=]\s*|\s+))(?<status>[4-5]\d\d)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _ApiErrorLine = new Regex(
            @"^API Error:\s*(?<status>[4-5]\d\d)(?:\s+(?<body>\{.*\}))?\s*$",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex _GoogleRpcError = new Regex(
            @"(?:^\s*|""status""\s*:\s*""|(?:\bgoogle\.rpc\b|\bGoogle RPC\b|\bRPC status\b)[^\r\n]{0,80}\b)"
            + @"(?<type>RESOURCE_EXHAUSTED|UNAUTHENTICATED|PERMISSION_DENIED)\b",
            RegexOptions.Compiled);

        private static readonly Regex _ClaudeUsageLimit = new Regex(
            @"^Claude AI usage limit reached\|[0-9]{9,11}$",
            RegexOptions.Compiled);

        /// <summary>Read an HTTP status or explicit Google RPC code from one stderr line.</summary>
        /// <param name="line">One raw stderr line.</param>
        /// <returns>Compact provider evidence, or null when the line has no supported cue.</returns>
        public static RuntimeProviderError? ParseStatusCue(string? line)
        {
            if (String.IsNullOrWhiteSpace(line)) return null;

            Match match = _StatusCue.Match(line);
            if (match.Success && Int32.TryParse(match.Groups["status"].Value, out int status))
                return new RuntimeProviderError { HttpStatusCode = status };

            Match rpcMatch = _GoogleRpcError.Match(line);
            if (rpcMatch.Success)
            {
                string errorType = rpcMatch.Groups["type"].Value switch
                {
                    "RESOURCE_EXHAUSTED" => "rate_limit_exceeded",
                    "UNAUTHENTICATED" => "authentication_error",
                    "PERMISSION_DENIED" => "permission_error",
                    _ => String.Empty
                };
                if (!String.IsNullOrEmpty(errorType)) return new RuntimeProviderError { ErrorType = errorType };
            }

            return null;
        }

        /// <summary>Read status and a machine type/code from an exact provider API error line.</summary>
        /// <param name="errorText">Provider error text beginning with <c>API Error:</c>.</param>
        /// <returns>Compact typed evidence, or null when no supported field is present.</returns>
        public static RuntimeProviderError? ParseApiError(string? errorText)
        {
            if (String.IsNullOrEmpty(errorText)) return null;

            Match match = _ApiErrorLine.Match(errorText);
            if (!match.Success || !Int32.TryParse(match.Groups["status"].Value, out int status)) return null;

            string? errorType = ReadMachineErrorType(match.Groups["body"].Value);
            return new RuntimeProviderError
            {
                HttpStatusCode = status,
                ErrorType = errorType
            };
        }

        /// <summary>Read Claude's exact usage-limit result protocol, which carries a reset epoch in its text.</summary>
        /// <param name="resultText">Errored Claude result text.</param>
        /// <returns>Usage-limit evidence, or null when the exact protocol line does not match.</returns>
        public static RuntimeProviderError? ParseClaudeUsageLimit(string? resultText)
        {
            if (String.IsNullOrEmpty(resultText) || !_ClaudeUsageLimit.IsMatch(resultText)) return null;
            return new RuntimeProviderError { ErrorType = "usage_limit_reached" };
        }

        private static string? ReadMachineErrorType(string body)
        {
            if (String.IsNullOrEmpty(body)) return null;
            try
            {
                ProviderErrorEnvelope? envelope = JsonSerializer.Deserialize<ProviderErrorEnvelope>(body);
                if (envelope == null) return null;
                if (!String.IsNullOrWhiteSpace(envelope.Error?.Type)) return envelope.Error!.Type;
                if (!String.IsNullOrWhiteSpace(envelope.Error?.Code)) return envelope.Error!.Code;
                if (!String.IsNullOrWhiteSpace(envelope.Type)) return envelope.Type;
                return envelope.Code;
            }
            catch (JsonException)
            {
                return null;
            }
        }

    }
}
