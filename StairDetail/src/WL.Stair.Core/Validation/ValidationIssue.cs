using System;

namespace WL.Stair.Core.Validation
{
    public sealed class ValidationIssue
    {
        public ValidationIssue(string code, ValidationSeverity severity, string parameterName, string message)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("An issue code is required.", nameof(code));
            }

            Code = code;
            Severity = severity;
            ParameterName = parameterName ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public string Code { get; }

        public ValidationSeverity Severity { get; }

        public string ParameterName { get; }

        public string Message { get; }
    }
}

