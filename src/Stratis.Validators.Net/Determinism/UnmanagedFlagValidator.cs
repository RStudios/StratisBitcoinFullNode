﻿using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace Stratis.Validators.Net.Determinism
{
    /// <summary>
    /// Validates that a <see cref="Mono.Cecil.MethodDefinition"/> is not unmanaged
    /// </summary>
    public class UnmanagedFlagValidator : IMethodDefinitionValidator
    {
        public static string ErrorType = "Unmanaged Flag Set";

        public IEnumerable<ValidationResult> Validate(MethodDefinition method)
        {
            // Instruction accesses external info.
            var invalid = method.IsUnmanaged;

            if (invalid)
            {
                return new List<ValidationResult>
                {
                    new ValidationResult(
                        method.Name,
                        method.FullName,
                        ErrorType,
                        $"Use of {method.FullName} is non-deterministic [{ErrorType}]")
                };
            }

            return Enumerable.Empty<ValidationResult>();
        }
    }
}