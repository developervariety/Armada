namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Models;

    /// <summary>Stores private durable intents for runner-owned Git checkouts.</summary>
    internal sealed partial class HarborCheckoutJournal
    {
        private const int MaximumRecords = 1024;
        private const int MaximumRecordBytes = 64 * 1024;
        private static readonly object _Sync = new object();
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly string _Root;

        /// <summary>Opens the private durable journal beneath the configured runner state directory.</summary>
        /// <param name="stateDirectory">Private root used by the runner.</param>
        public HarborCheckoutJournal(string stateDirectory)
        {
            if (String.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("A checkout journal root is required.", nameof(stateDirectory));
            string stateRoot = Path.GetFullPath(stateDirectory);
            HarborPrivateStorage.EnsurePrivateDirectory(stateRoot);
            _Root = Path.Combine(stateRoot, "workspace-checkouts");
            HarborPrivateStorage.EnsurePrivateDirectory(_Root);
        }

        /// <summary>Creates one immutable checkout intent before runner-side Git mutation.</summary>
        /// <param name="intent">Bound owner and checkout identity to persist.</param>
        public void Create(HarborCheckoutIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            ValidateIntent(intent);
            lock (_Sync)
            {
                List<HarborCheckoutIntent> existing = EnumerateUnsafe();
                if (existing.Count >= MaximumRecords) throw Failure("checkout_limit_reached", "The runner has reached its retained checkout limit.");
                if (existing.Exists(value => String.Equals(value.Binding.CheckoutId, intent.Binding.CheckoutId, StringComparison.Ordinal)))
                    throw Failure("checkout_id_conflict", "The checkout identifier is already recorded.");

                string staging = Path.Combine(_Root, ".pending-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant());
                string visible = DirectoryFor(intent.Binding.CheckoutId);
                try
                {
                    HarborPrivateStorage.EnsurePrivateDirectory(staging);
                    WriteNew(Path.Combine(staging, "checkout.json"), intent);
                    Directory.Move(staging, visible);
                }
                catch
                {
                    DeleteUnpublishedStaging(staging);
                    throw;
                }
            }
        }

        /// <summary>Reads the durable intent for one opaque checkout identifier.</summary>
        /// <param name="checkoutId">Runner-issued checkout identifier.</param>
        /// <returns>The validated durable intent.</returns>
        public HarborCheckoutIntent Read(string checkoutId)
        {
            ValidateCheckoutId(checkoutId);
            lock (_Sync) return ReadUnsafe(checkoutId);
        }

        /// <summary>Reads every published checkout intent and fails on malformed visible records.</summary>
        /// <returns>Validated checkout intents.</returns>
        public List<HarborCheckoutIntent> Enumerate()
        {
            lock (_Sync) return EnumerateUnsafe();
        }

        /// <summary>Updates lifecycle fields without changing the recorded checkout binding.</summary>
        /// <param name="intent">Updated intent with the original immutable binding.</param>
        public void Update(HarborCheckoutIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            ValidateIntent(intent);
            lock (_Sync)
            {
                string path = RecordPath(intent.Binding.CheckoutId);
                HarborCheckoutIntent stored = ReadUnsafe(intent.Binding.CheckoutId);
                if (!SameBinding(stored, intent)) throw Failure("checkout_binding_mismatch", "A checkout binding is immutable.");
                WriteAtomic(path, intent);
            }
        }

    }
}
