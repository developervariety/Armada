namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Estimates the cost of one typed-decision call from the configured price table and the tokens the
    /// call carries. A batched item already carries its share of the request's tokens, so its estimate is
    /// its share of the request's cost and the estimates of a batch add up to the request's cost.
    /// </summary>
    public static class TypedDecisionCostEstimator
    {
        #region Public-Members

        /// <summary>Reason: the call has no answer, so it reports no tokens to price.</summary>
        public const string CallUnavailableReason = "call_unavailable";

        /// <summary>Reason: the provider did not report which model ran.</summary>
        public const string ModelNotReportedReason = "model_not_reported";

        /// <summary>Reason: the price table names no price for the model that ran.</summary>
        public const string NoPriceForModelReason = "no_price_for_model";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Estimate the cost of one call.
        /// </summary>
        /// <param name="table">The price table, or null for none.</param>
        /// <param name="result">The call's result, or null.</param>
        /// <returns>The estimate, or a null cost with its reason.</returns>
        public static TypedDecisionCostEstimate Estimate(TypedDecisionPriceTable? table, TypedDecisionResult? result)
        {
            TypedDecisionCostEstimate estimate = new TypedDecisionCostEstimate
            {
                PriceTableVersion = table?.Version ?? "unversioned"
            };

            if (result == null || !result.Available)
            {
                estimate.UnavailableReason = CallUnavailableReason;
                return estimate;
            }

            if (String.IsNullOrWhiteSpace(result.Model))
            {
                estimate.UnavailableReason = ModelNotReportedReason;
                return estimate;
            }

            TypedDecisionModelPrice? price = FindPrice(table, result.Model!);
            if (price == null)
            {
                estimate.UnavailableReason = NoPriceForModelReason;
                return estimate;
            }

            double usd = (Math.Max(0, result.InputTokens) * price.InputUsdPerMillionTokens
                + Math.Max(0, result.OutputTokens) * price.OutputUsdPerMillionTokens) / 1_000_000.0;
            estimate.EstimatedCostUsd = Math.Round(usd, 8);
            return estimate;
        }

        #endregion

        #region Private-Methods

        private static TypedDecisionModelPrice? FindPrice(TypedDecisionPriceTable? table, string model)
        {
            if (table?.Models == null || table.Models.Count == 0) return null;

            string? bestKey = null;
            TypedDecisionModelPrice? best = null;
            foreach (KeyValuePair<string, TypedDecisionModelPrice> entry in table.Models)
            {
                if (String.IsNullOrWhiteSpace(entry.Key) || entry.Value == null) continue;
                if (String.Equals(entry.Key, model, StringComparison.OrdinalIgnoreCase)) return entry.Value;
                if (model.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase)
                    && (bestKey == null || entry.Key.Length > bestKey.Length))
                {
                    bestKey = entry.Key;
                    best = entry.Value;
                }
            }

            return best;
        }

        #endregion
    }
}
