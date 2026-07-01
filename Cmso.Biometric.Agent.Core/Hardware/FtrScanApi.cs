using System.Runtime.InteropServices;

namespace Cmso.Biometric.Agent.Core.Hardware
{
    /// <summary>
    /// P/Invoke wrappers para ftrScanAPI.dll — Futronic Fingerprint Scanner.
    /// Assinaturas baseadas no header oficial do SDK Futronic FS80H.
    /// </summary>
    internal static class FtrScanApi
    {
        private const string DllName = "ftrScanAPI.dll";

        // ─────────────────────────────────────────────────────────────────
        // Structs que espelham as estruturas C++ da DLL
        // ─────────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct ParametrosFakeReplica
        {
            public int bCalculated;
            public int nCalculatedSum1;
            public int nCalculatedSumFuzzy;
            public int nCalculatedSumEmpty;
            public int nCalculatedSum2;
            public double dblCalculatedTremor;
            public double dblCalculatedValue;
        }

        /// <summary>
        /// Estrutura de parâmetros de frame preenchida por ftrScanIsFingerPresent.
        /// nContrastOnDose2 > limiar indica dedo presente.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ParametrosFrame
        {
            public int nContrastOnDose2;
            public int nContrastOnDose4;
            public int nDose;
            public int nBrightnessOnDose1;
            public int nBrightnessOnDose2;
            public int nBrightnessOnDose3;
            public int nBrightnessOnDose4;
            public ParametrosFakeReplica FakeReplicaParams;
            public ParametrosFakeReplica Reserved;
        }

        /// <summary>
        /// Dimensões e tamanho do buffer de imagem retornado por ftrScanGetImageSize.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TamanhoImagem
        {
            public int nWidth;
            public int nHeight;
            public int nImageSize;
        }

        // ─────────────────────────────────────────────────────────────────
        // P/Invoke
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Abre o dispositivo. Retorna IntPtr.Zero em caso de falha.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ftrScanOpenDevice();

        /// <summary>Fecha o dispositivo identificado pelo handle.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ftrScanCloseDevice(IntPtr ftrHandle);

        /// <summary>
        /// Verifica se há dedo presente no leitor.
        /// pFrameParameters é preenchido com dados de contraste — use nContrastOnDose2 para detecção.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrScanIsFingerPresent(
            IntPtr ftrHandle,
            out ParametrosFrame pFrameParameters);

        /// <summary>Obtém as dimensões e tamanho do buffer de imagem.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrScanGetImageSize(
            IntPtr ftrHandle,
            out TamanhoImagem pImageSize);

        /// <summary>
        /// Captura a imagem da digital e preenche o buffer.
        /// nDose: dose de captura (4 é a qualidade padrão).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrScanGetImage(
            IntPtr ftrHandle,
            int nDose,
            byte[] pBuffer);

        /// <summary>Define o estado dos LEDs verde e vermelho (255 = ligado, 0 = desligado).</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrScanSetDiodesStatus(
            IntPtr ftrHandle,
            byte byGreenDiodeStatus,
            byte byRedDiodeStatus);

        /// <summary>Lê o estado atual dos LEDs verde e vermelho.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ftrScanGetDiodesStatus(
            IntPtr ftrHandle,
            out bool pbIsGreenDiodeOn,
            out bool pbIsRedDiodeOn);

        /// <summary>Retorna o código do último erro ocorrido na DLL.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ftrScanGetLastError();

        // ─────────────────────────────────────────────────────────────────
        // Constantes
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Limiar de contraste (nContrastOnDose2) para considerar dedo presente.
        /// Baseado em testes com FS80H — valor mais baixo para melhor detecção.
        /// </summary>
        internal const int ContrasteMinimoDeteccao = 20;

        /// <summary>Dose padrão de captura (qualidade equilibrada).</summary>
        internal const int DosePadrao = 4;
    }
}
