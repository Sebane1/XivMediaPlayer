using System;
using System.Text;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing
{
    /// <summary>
    /// Wraps D3DCompile with detailed error reporting for embedded HLSL sources.
    /// </summary>
    internal static class ShaderCompileHelper
    {
        /// <summary>Receives shader compile failures and warnings (e.g. plugin log).</summary>
        public static Action<string>? LogIssue { get; set; }

        public static bool TryCompile(
            string shaderSource,
            string entryPoint,
            string sourceName,
            string profile,
            out ReadOnlyMemory<byte> bytecode,
            out string errorDetail)
        {
            bytecode = default;
            errorDetail = string.Empty;

            Result result = Compiler.Compile(shaderSource, entryPoint, sourceName, profile, out Blob blob, out Blob errorBlob);
            try
            {
                if (result.Failure)
                {
                    errorDetail = FormatCompileFailure(sourceName, entryPoint, profile, result, errorBlob);
                    LogIssue?.Invoke(errorDetail);
                    return false;
                }

                string warnings = errorBlob?.AsString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(warnings))
                {
                    LogIssue?.Invoke($"[{sourceName}] {entryPoint} ({profile}) compiler output:\n{warnings}");
                }

                ReadOnlyMemory<byte> compiled = blob.AsMemory();
                blob.Dispose();
                bytecode = compiled;
                return true;
            }
            finally
            {
                errorBlob?.Dispose();
                if (result.Failure)
                {
                    blob?.Dispose();
                }
            }
        }

        public static ReadOnlyMemory<byte> CompileRequired(
            string shaderSource,
            string entryPoint,
            string sourceName,
            string profile)
        {
            if (!TryCompile(shaderSource, entryPoint, sourceName, profile, out ReadOnlyMemory<byte> bytecode, out string errorDetail))
            {
                throw new InvalidOperationException(errorDetail);
            }

            return bytecode;
        }

        private static string FormatCompileFailure(
            string sourceName,
            string entryPoint,
            string profile,
            Result result,
            Blob? errorBlob)
        {
            var sb = new StringBuilder();
            sb.Append("[shader] Compilation failed: ");
            sb.Append(sourceName);
            sb.Append(" :: ");
            sb.Append(entryPoint);
            sb.Append(" (");
            sb.Append(profile);
            sb.Append(") HRESULT 0x");
            sb.Append(result.Code.ToString("X8"));

            string compilerText = errorBlob?.AsString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(compilerText))
            {
                sb.AppendLine();
                sb.Append(compilerText);
            }
            else
            {
                sb.Append(" — D3DCompile returned no diagnostic text.");
            }

            return sb.ToString();
        }
    }
}
