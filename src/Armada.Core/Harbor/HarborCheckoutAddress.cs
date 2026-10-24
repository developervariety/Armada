namespace Armada.Core.Harbor
{
    using System;

    /// <summary>Opaque checkout address that never represents an Admiral filesystem path.</summary>
    public sealed class HarborCheckoutAddress
    {
        /// <summary>Durable dock owning this address.</summary>
        public string DockId { get; private set; } = String.Empty;
        /// <summary>Optional journaled auxiliary checkout identifier.</summary>
        public string? CheckoutId { get; private set; }
        /// <summary>Select the configured repository.</summary>
        public bool UseRepository { get; private set; }
        /// <summary>Select the configured primary landing checkout.</summary>
        public bool UseWorkingDirectory { get; private set; }
        /// <summary>Optional path beneath the selected checkout.</summary>
        public string RelativePath { get; private set; } = String.Empty;

        private HarborCheckoutAddress() { }

        /// <summary>Whether a value claims the Harbor address scheme, including malformed addresses.</summary>
        public static bool IsHarbor(string? value) => value != null
            && value.StartsWith("harbor:", StringComparison.OrdinalIgnoreCase);

        /// <summary>Build an opaque address for a durable dock.</summary>
        public static string ForDock(string dockId)
        {
            if (!ValidSegment(dockId)) throw new ArgumentException("harbor_checkout_address_invalid", nameof(dockId));
            return "harbor:/dock/" + dockId;
        }

        /// <summary>Parse an opaque address without host path normalization or case folding.</summary>
        public static bool TryParse(string? value, out HarborCheckoutAddress? address)
        {
            address = null;
            const string prefix = "harbor:/dock/";
            if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = value.Substring(prefix.Length).Replace('\\', '/').Split('/');
            if (parts.Length == 0 || !ValidSegment(parts[0])) return false;
            HarborCheckoutAddress parsed = new HarborCheckoutAddress { DockId = parts[0] };
            int next = 1;
            if (next < parts.Length && parts[next] == "repository") { parsed.UseRepository = true; next++; }
            else if (next < parts.Length && parts[next] == "working") { parsed.UseWorkingDirectory = true; next++; }
            else if (next < parts.Length && parts[next] == "checkout")
            {
                if (++next >= parts.Length || !ValidSegment(parts[next])) return false;
                parsed.CheckoutId = parts[next++];
            }
            if (next < parts.Length)
            {
                if (parsed.UseRepository || parts[next++] != "files" || next >= parts.Length) return false;
                for (int index = next; index < parts.Length; index++)
                    if (!ValidSegment(parts[index])) return false;
                parsed.RelativePath = String.Join("/", parts, next, parts.Length - next);
            }
            address = parsed;
            return true;
        }

        private static bool ValidSegment(string? value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 900 || value == "." || value == "..") return false;
            foreach (char character in value)
                if (Char.IsControl(character) || character == '/' || character == '\\'
                    || character == ':' || character == '%' || character == '?' || character == '#') return false;
            return true;
        }
    }
}
