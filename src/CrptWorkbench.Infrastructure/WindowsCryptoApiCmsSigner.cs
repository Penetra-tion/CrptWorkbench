using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CrptWorkbench.Infrastructure;

internal static class WindowsCryptoApiCmsSigner
{
    private const uint X509AsnEncoding = 0x00000001;
    private const uint Pkcs7AsnEncoding = 0x00010000;

    internal static byte[] Sign(
        byte[] content,
        X509Certificate2 certificate,
        string digestAlgorithmOid,
        bool detached = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(digestAlgorithmOid);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "CryptoPro GOST CMS signing requires the Windows CryptoAPI.");
        }

        var digestOidPointer = IntPtr.Zero;
        var certificateArrayPointer = IntPtr.Zero;
        GCHandle contentHandle = default;

        try
        {
            digestOidPointer = Marshal.StringToHGlobalAnsi(digestAlgorithmOid);
            certificateArrayPointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(certificateArrayPointer, certificate.Handle);
            contentHandle = GCHandle.Alloc(content, GCHandleType.Pinned);

            var parameters = new CryptSignMessageParameters
            {
                Size = (uint)Marshal.SizeOf<CryptSignMessageParameters>(),
                MessageEncodingType = X509AsnEncoding | Pkcs7AsnEncoding,
                SigningCertificate = certificate.Handle,
                HashAlgorithm = new CryptAlgorithmIdentifier
                {
                    ObjectId = digestOidPointer
                },
                MessageCertificateCount = 1,
                MessageCertificates = certificateArrayPointer
            };

            var messagePointers = new[] { contentHandle.AddrOfPinnedObject() };
            var messageSizes = new[] { checked((uint)content.Length) };
            uint signatureSize = 0;

            if (!CryptSignMessage(
                    ref parameters,
                    detachedSignature: detached,
                    1,
                    messagePointers,
                    messageSizes,
                    null,
                    ref signatureSize))
            {
                ThrowLastCryptographicError();
            }

            var signature = new byte[signatureSize];
            if (!CryptSignMessage(
                    ref parameters,
                    detachedSignature: detached,
                    1,
                    messagePointers,
                    messageSizes,
                    signature,
                    ref signatureSize))
            {
                CryptographicOperations.ZeroMemory(signature);
                ThrowLastCryptographicError();
            }

            if (signatureSize != signature.Length)
            {
                Array.Resize(ref signature, checked((int)signatureSize));
            }

            return signature;
        }
        finally
        {
            if (contentHandle.IsAllocated)
            {
                contentHandle.Free();
            }

            if (certificateArrayPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(certificateArrayPointer);
            }

            if (digestOidPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(digestOidPointer);
            }
        }
    }

    private static void ThrowLastCryptographicError()
    {
        var error = Marshal.GetLastWin32Error();
        throw new CryptographicException(error);
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptSignMessage(
        ref CryptSignMessageParameters signParameters,
        [MarshalAs(UnmanagedType.Bool)] bool detachedSignature,
        uint toBeSignedCount,
        [In] IntPtr[] toBeSigned,
        [In] uint[] toBeSignedSizes,
        [Out] byte[]? signedBlob,
        ref uint signedBlobSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptDataBlob
    {
        internal uint Size;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptAlgorithmIdentifier
    {
        internal IntPtr ObjectId;
        internal CryptDataBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptSignMessageParameters
    {
        internal uint Size;
        internal uint MessageEncodingType;
        internal IntPtr SigningCertificate;
        internal CryptAlgorithmIdentifier HashAlgorithm;
        internal IntPtr HashAuxInfo;
        internal uint MessageCertificateCount;
        internal IntPtr MessageCertificates;
        internal uint MessageCrlCount;
        internal IntPtr MessageCrls;
        internal uint AuthenticatedAttributeCount;
        internal IntPtr AuthenticatedAttributes;
        internal uint UnauthenticatedAttributeCount;
        internal IntPtr UnauthenticatedAttributes;
        internal uint Flags;
        internal uint InnerContentType;
        internal CryptAlgorithmIdentifier HashEncryptionAlgorithm;
        internal IntPtr HashEncryptionAuxInfo;
    }
}
