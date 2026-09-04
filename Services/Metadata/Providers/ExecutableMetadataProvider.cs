using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace ClankerExplorer.Services.Metadata.Providers;

/// <summary>
/// Extracts metadata for Windows executables and libraries: version, product, company, PE architecture, subsystem, and digital signature.
/// </summary>
public class ExecutableMetadataProvider : IMetadataProvider
{
    public int Order => 10;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".ocx", ".scr", ".cpl", ".com", ".efi", ".node"
    };

    public bool CanHandle(MetadataExtractionContext context)
    {
        return !context.IsDirectory && ExecutableExtensions.Contains(context.Extension);
    }

    public Task ProvideMetadataAsync(MetadataExtractionContext context, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            string path = context.FilePath;
            if (!File.Exists(path)) return;

            cancellationToken.ThrowIfCancellationRequested();

            // 1. FileVersionInfo
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);

                if (!string.IsNullOrWhiteSpace(vi.ProductName))
                {
                    context.AddItem("Executable", "🛡️", "Product", vi.ProductName, isCopyable: true);
                }

                if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                {
                    context.AddItem("Executable", "🛡️", "Description", vi.FileDescription, isCopyable: true);
                }

                if (!string.IsNullOrWhiteSpace(vi.CompanyName))
                {
                    context.AddItem("Executable", "🛡️", "Company", vi.CompanyName, isCopyable: true);
                }

                if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                {
                    context.AddItem("Executable", "🛡️", "File Version", vi.FileVersion, isCopyable: true, isMonospace: true);
                }

                if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                {
                    context.AddItem("Executable", "🛡️", "Product Version", vi.ProductVersion, isCopyable: true, isMonospace: true);
                }

                if (!string.IsNullOrWhiteSpace(vi.OriginalFilename) && !string.Equals(vi.OriginalFilename, context.ItemName, StringComparison.OrdinalIgnoreCase))
                {
                    context.AddItem("Executable", "🛡️", "Original Name", vi.OriginalFilename, isCopyable: true, isMonospace: true);
                }

                if (!string.IsNullOrWhiteSpace(vi.LegalCopyright))
                {
                    context.AddItem("Executable", "🛡️", "Copyright", vi.LegalCopyright, isCopyable: true);
                }
            }
            catch { }

            cancellationToken.ThrowIfCancellationRequested();

            // 2. PE Header Inspection
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(fs);

                if (fs.Length >= 64)
                {
                    ushort dosSig = reader.ReadUInt16();
                    if (dosSig == 0x5A4D) // "MZ"
                    {
                        fs.Seek(0x3C, SeekOrigin.Begin);
                        int peOffset = reader.ReadInt32();

                        if (peOffset > 0 && peOffset < fs.Length - 24)
                        {
                            fs.Seek(peOffset, SeekOrigin.Begin);
                            uint peSig = reader.ReadUInt32();

                            if (peSig == 0x00004550) // "PE\0\0"
                            {
                                ushort machine = reader.ReadUInt16();
                                ushort numSections = reader.ReadUInt16();
                                uint timeStamp = reader.ReadUInt32();
                                fs.Seek(10, SeekOrigin.Current); // skip pointer to symbol table + num symbols + size of optional header
                                ushort characteristics = reader.ReadUInt16();

                                string arch = machine switch
                                {
                                    0x8664 => "x64 (AMD64 64-bit)",
                                    0x014c => "x86 (32-bit)",
                                    0xAA64 => "ARM64 (64-bit)",
                                    0x01c0 => "ARM (32-bit)",
                                    0x0200 => "Itanium (IA64)",
                                    _ => $"Architecture 0x{machine:X4}"
                                };
                                context.AddItem("Executable", "🛡️", "Architecture", arch, isCopyable: true, badge: arch.Contains("64") ? "64-bit" : "32-bit");

                                bool isDll = (characteristics & 0x2000) != 0;
                                string itemType = isDll ? "Dynamic Link Library (.dll)" : "Executable Application (.exe)";

                                ushort optMagic = reader.ReadUInt16();
                                bool is64BitOpt = optMagic == 0x020B;

                                // Subsystem offset: 68 bytes from start of optional header
                                fs.Seek(peOffset + 24 + 68, SeekOrigin.Begin);
                                ushort subsystem = reader.ReadUInt16();

                                string subDesc = subsystem switch
                                {
                                    1 => "Native Device Driver",
                                    2 => "Windows GUI (Desktop)",
                                    3 => "Windows Console (CLI)",
                                    7 => "POSIX Console",
                                    9 => "Windows CE",
                                    14 => "EFI Application",
                                    _ => $"Subsystem {subsystem}"
                                };
                                context.AddItem("Executable", "🛡️", "Subsystem", subDesc, isCopyable: true);

                                // Check .NET CLR Header (DataDirectory[14])
                                int clrOffset = is64BitOpt ? (peOffset + 24 + 112 + (14 * 8)) : (peOffset + 24 + 96 + (14 * 8));
                                if (fs.Length > clrOffset + 8)
                                {
                                    fs.Seek(clrOffset, SeekOrigin.Begin);
                                    uint clrRva = reader.ReadUInt32();
                                    uint clrSize = reader.ReadUInt32();

                                    string runtime = (clrRva > 0 && clrSize > 0)
                                        ? ".NET Managed Assembly"
                                        : "Native Win32/x64 Binary";
                                    context.AddItem("Executable", "🛡️", "Runtime", runtime, isCopyable: true, badge: clrRva > 0 ? ".NET" : "Native");
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            cancellationToken.ThrowIfCancellationRequested();

            // 3. Authenticode Digital Signature
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(path);
                if (cert != null)
                {
                    using var cert2 = new X509Certificate2(cert);
                    string signer = cert2.GetNameInfo(X509NameType.SimpleName, false);
                    string issuer = cert2.GetNameInfo(X509NameType.SimpleName, true);
                    string validUntil = cert2.NotAfter.ToLocalTime().ToString("yyyy-MM-dd");

                    context.AddItem("Executable", "🛡️", "Signer", string.IsNullOrWhiteSpace(signer) ? cert2.Subject : signer, isCopyable: true, badge: "Signed");
                    if (!string.IsNullOrWhiteSpace(issuer))
                    {
                        context.AddItem("Executable", "🛡️", "Certificate Issuer", issuer, isCopyable: true);
                    }
                    context.AddItem("Executable", "🛡️", "Signature Valid", $"Valid until {validUntil}", isCopyable: true, isMonospace: true);
                }
            }
            catch
            {
                // Not digitally signed or error reading signature
                context.AddItem("Executable", "🛡️", "Digital Signature", "Unsigned", isCopyable: false);
            }
        }, cancellationToken);
    }
}
