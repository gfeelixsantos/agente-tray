using System.Runtime.InteropServices;

namespace Cmso.Biometric.Agent.Core.Hardware
{
    /// <summary>
    /// P/Invoke wrappers para ftrAnsiSdk.dll — Futronic ANSI/ISO SDK.
    /// Assinaturas baseadas no header oficial ftrAnsiSdk.h.
    /// Referência: https://github.com/MuhammdAli/Futuronic_C_Sharp_wrapper/blob/master/ftrAnsiSdk.h
    /// </summary>
    internal static class FtrAnsiSdk
    {
        private const string DllName = "ftrAnsiSdk.dll";

        // ─────────────────────────────────────────────────────────────────
        // Funções exportadas
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Retorna o tamanho máximo em bytes de um template ANSI.
        /// int ftrAnsiSdkGetMaxTemplateSize();
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ftrAnsiSdkGetMaxTemplateSize();

        /// <summary>
        /// Cria template ANSI a partir de buffer de imagem já capturado.
        /// FTR_BOOL ftrAnsiSdkCreateTemplateFromBuffer(
        ///     FTRHANDLE ftrHandle, FTR_BYTE byFingerPosition,
        ///     FTR_PVOID pImageBuffer, int nWidth, int nHeight,
        ///     FTR_PVOID pOutTemplate, int* pnOutTemplateSize);
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrAnsiSdkCreateTemplateFromBuffer(
            IntPtr ftrHandle,
            byte fingerPosition,
            byte[] imageBuffer,
            int width,
            int height,
            byte[] outTemplate,
            ref int outTemplateSize);

        /// <summary>
        /// Compara dois templates ANSI. Retorna score de similaridade (float).
        /// FTR_BOOL ftrAnsiSdkMatchTemplates(
        ///     FTR_PVOID pProbeTemplate, FTR_PVOID pGalleryTemplate, float* pfOutResult);
        /// Thresholds: Low=37, LowMedium=65, Medium=93, HighMedium=121, High=146, VeryHigh=189
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrAnsiSdkMatchTemplates(
            byte[] probeTemplate,
            byte[] galleryTemplate,
            out float score);

        // ─────────────────────────────────────────────────────────────────
        // Constantes
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Posição do dedo (ANSI 378) — usamos IndicadorDireito como padrão.</summary>
        internal const byte FingerPositionDefault = 0x02; // RightIndex

        /// <summary>
        /// Threshold padrão de matching (score float).
        /// 70 equivale a FAR ~ 0.01% — balanceia segurança e usabilidade.
        /// </summary>
        internal const float MatchThresholdDefault = 70f;
    }
}
