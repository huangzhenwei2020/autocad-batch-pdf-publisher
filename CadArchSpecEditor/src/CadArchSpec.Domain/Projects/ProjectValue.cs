using System;
using CadArchSpec.Domain.Common;

namespace CadArchSpec.Domain.Projects
{
    public sealed class ProjectValue<T>
    {
        public T Value { get; set; }
        public ValueState State { get; set; }
        public string Source { get; set; } = string.Empty;
        public string SourceDocumentId { get; set; } = string.Empty;
        public string EnteredBy { get; set; } = string.Empty;
        public DateTimeOffset? ConfirmedAt { get; set; }
        public bool IsManuallyOverridden { get; set; }
        public string OverrideReason { get; set; } = string.Empty;

        public bool HasConfirmedValue
        {
            get
            {
                return State == ValueState.Confirmed || State == ValueState.Overridden;
            }
        }
    }
}
