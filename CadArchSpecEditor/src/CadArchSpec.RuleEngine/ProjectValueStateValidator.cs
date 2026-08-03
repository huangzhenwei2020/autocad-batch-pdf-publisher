using System;
using CadArchSpec.Domain.Common;
using CadArchSpec.Domain.Projects;

namespace CadArchSpec.RuleEngine
{
    public sealed class ProjectValueStateValidator
    {
        public bool CanBeUsedForDeterministicReview<T>(ProjectValue<T> value)
        {
            return value != null && (value.State == ValueState.Confirmed || value.State == ValueState.Overridden);
        }

        public void Confirm<T>(ProjectValue<T> value, T confirmedValue, string source, string user, DateTimeOffset confirmedAt)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("确认值必须记录来源。", nameof(source));
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("确认值必须记录确认人。", nameof(user));
            value.Value = confirmedValue;
            value.State = ValueState.Confirmed;
            value.Source = source.Trim();
            value.EnteredBy = user.Trim();
            value.ConfirmedAt = confirmedAt;
            value.IsManuallyOverridden = false;
            value.OverrideReason = string.Empty;
        }

        public void Override<T>(ProjectValue<T> value, T overriddenValue, string reason, string user, DateTimeOffset confirmedAt)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("人工覆盖必须填写原因。", nameof(reason));
            value.Value = overriddenValue;
            value.State = ValueState.Overridden;
            value.EnteredBy = user ?? string.Empty;
            value.ConfirmedAt = confirmedAt;
            value.IsManuallyOverridden = true;
            value.OverrideReason = reason.Trim();
        }
    }
}
