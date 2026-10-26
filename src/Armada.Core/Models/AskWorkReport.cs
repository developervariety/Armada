namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>A fresh observation of work explicitly returned by an approved action.</summary>
    public sealed class AskWorkReport
    {
        /// <summary>Kind of recorded work.</summary>
        public AskWorkKindEnum Kind { get; set; }
        /// <summary>Exact identifier returned by the tool or its job.</summary>
        public string Id { get; set; } = String.Empty;
        /// <summary>Current record title.</summary>
        public string Title { get; set; } = String.Empty;
        /// <summary>Current recorded lifecycle status.</summary>
        public string Status { get; set; } = String.Empty;
        /// <summary>Delivery observation, separate from action success.</summary>
        public string DeliveryState { get; set; } = String.Empty;
        /// <summary>Recorded parent mission, when this is a child mission.</summary>
        public string? ParentMissionId { get; set; }
        /// <summary>Recorded rescue child, when one is known.</summary>
        public string? RescueMissionId { get; set; }
        /// <summary>Whether recorded evidence establishes produced work.</summary>
        public bool ProducedWork { get; set; }
        /// <summary>Whether authoritative delivery evidence establishes delivery.</summary>
        public bool Delivered { get; set; }
        /// <summary>Bounded explanation of this observation.</summary>
        public string Summary { get; set; } = String.Empty;
        /// <summary>Last recorded update, when known.</summary>
        public DateTime? UpdatedUtc { get; set; }
    }
}
