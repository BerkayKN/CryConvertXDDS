using System;
using System.IO;
using System.Linq;

namespace CryConvertXDDS
{
    class Program
    {
		// I made this with AI so it might not be the best code you have seen
        static void Main(string[] args)
        {
            // Batch folder mode: -f <folder>
            if (args.Length == 2 && (args[0] == "-f" || args[0] == "--folder"))
            {
                string inputFolder = args[1];
                if (!Directory.Exists(inputFolder))
                {
                    Console.WriteLine($"[Error] Input folder not found: {inputFolder}");
                    return;
                }
                string outputFolder = inputFolder.TrimEnd(Path.DirectorySeparatorChar) + "_conversion";
                Directory.CreateDirectory(outputFolder);
                // Recursively find all .dds files
                string[] ddsFiles = Directory.GetFiles(inputFolder, "*.dds", SearchOption.AllDirectories);
                if (ddsFiles.Length == 0)
                {
                    Console.WriteLine($"[Info] No .dds files found in {inputFolder}");
                    return;
                }
                Console.WriteLine($"[Info] Batch converting {ddsFiles.Length} DDS files from {inputFolder} to {outputFolder} (preserving folder structure)");
                foreach (var file in ddsFiles)
                {
                    // Compute relative path from inputFolder
                    string relPath = GetRelativePath(inputFolder, file);
                    string outFile = Path.Combine(outputFolder, relPath);
                    string outDir = Path.GetDirectoryName(outFile);
                    if (!Directory.Exists(outDir))
                        Directory.CreateDirectory(outDir);
                    try
                    {
                        ProcessFile(file, outFile, noUnswizzle: false, noEndianSwap: false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to convert {file}: {ex.Message}");
                    }
                }
                Console.WriteLine("Batch conversion completed.");
                return;
            }

            if (args.Length == 1 && File.Exists(args[0]))
            {
                // Drag-and-drop mode: process the file
                string inputPath = args[0];
                string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                string inputDir = Path.GetDirectoryName(inputPath)?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
                string outputPath;
                if (string.Equals(inputDir, exeDir, StringComparison.OrdinalIgnoreCase))
                {
                    // If in the same directory as the program, add _conversion
                    outputPath = Path.Combine(inputDir, Path.GetFileNameWithoutExtension(inputPath) + "_conversion.dds");
                }
                else
                {
                    // Else, just use the original name in the same directory
                    outputPath = Path.Combine(inputDir, Path.GetFileName(inputPath));
                }
                Console.WriteLine($"[Info] Drag-and-drop mode: {inputPath} -> {outputPath}");
                ProcessFile(inputPath, outputPath, noUnswizzle: false, noEndianSwap: false);
                return;
            }

            if (args.Length < 2)
            {
                Console.WriteLine("Usage: CryConvertXDDS <input.dds> <output.dds> [-no-unswizzle] [-no-endianswap]\n       or drag-and-drop a DDS file onto the program\n       or -f <folder> to batch convert all DDS files in a folder");
                return;
            }

            string inputPathArg = args[0];
            string outputPathArg = args[1];
            bool noUnswizzle = args.Length > 2 && args[2] == "-no-unswizzle";
            bool noEndianSwap = args.Length > 2 && (args.Contains("-no-endianswap"));

            if (noUnswizzle && noEndianSwap)
            {
                Console.WriteLine("[Error] Both unswizzle and endian swap are skipped. Aborting.");
                return;
            }

            if (!File.Exists(inputPathArg))
            {
                Console.WriteLine($"[Error] Input file not found: {inputPathArg}");
                return;
            }

            ProcessFile(inputPathArg, outputPathArg, noUnswizzle, noEndianSwap);
        }

        static void ProcessFile(string inputPath, string outputPath, bool noUnswizzle, bool noEndianSwap)
        {
            byte[] ddsData = File.ReadAllBytes(inputPath);

            // DDS header is usually 128 bytes
            int headerSize = 128;
            byte[] header = new byte[headerSize];
            Array.Copy(ddsData, 0, header, 0, headerSize);

            // Read mipmap count from header (offset 28, 4 bytes)
            int mipMapCount = BitConverter.ToInt32(header, 28);
            if (mipMapCount <= 1) mipMapCount = 1; // If not set, treat as 1

            int width = BitConverter.ToInt32(header, 16);
            int height = BitConverter.ToInt32(header, 12);
            int bytesPerBlock = GetBlockSizeFromFourCC(header);

            int offset = 0;
            int imageDataSize = ddsData.Length - headerSize;
            byte[] imageData = new byte[imageDataSize];
            Array.Copy(ddsData, headerSize, imageData, 0, imageDataSize);

            byte[] outputImageData = new byte[imageDataSize];

            for (int mip = 0; mip < mipMapCount; mip++)
            {
                int mipWidth = Math.Max(1, width >> mip);
                int mipHeight = Math.Max(1, height >> mip);
                int blockWidth = GetBlockWidth(mipWidth);
                int blockHeight = GetBlockHeight(mipHeight);
                int mipSize = blockWidth * blockHeight * bytesPerBlock;

                if (offset + mipSize > imageData.Length)
                    break; // Prevent out-of-bounds

                byte[] mipInput = new byte[mipSize];
                Array.Copy(imageData, offset, mipInput, 0, mipSize);
                byte[] mipOutput = new byte[mipSize];

                // For small mipmaps (<= 16x16 blocks, i.e. <= 64x64 pixels), skip unswizzle (data is stored linear)
                bool doUntile = !(mipWidth <= 64 || mipHeight <= 64) && !noUnswizzle;
                if (doUntile)
                {
                    UntileXbox360(mipInput, mipOutput, blockWidth, blockHeight, bytesPerBlock);
                    if (!noEndianSwap)
                        FixEndian(mipOutput);
                }
                else
                {
                    Array.Copy(mipInput, mipOutput, mipSize);
                    if (!noEndianSwap)
                        FixEndian(mipOutput);
                }

                Array.Copy(mipOutput, 0, outputImageData, offset, mipSize);
                offset += mipSize;
            }

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                fs.Write(header, 0, header.Length);
                fs.Write(outputImageData, 0, outputImageData.Length);
            }

            Console.WriteLine($"Conversion completed. Output: {outputPath}");
        }

        static int GetBlockSizeFromFourCC(byte[] header)
        {
            // FourCC is at offset 84 in DDS header
            string fourCC = System.Text.Encoding.ASCII.GetString(header, 84, 4);
            switch (fourCC)
            {
                case "DXT1": return 8;
                case "DXT3": return 16;
                case "DXT5": return 16;
                case "ATI2": return 16; // BC5/3Dc/ATI2
                case "BC4U": return 8;  // BC4 unsigned
                case "BC4S": return 8;  // BC4 signed
                case "BC5U": return 16; // BC5 unsigned
                case "BC5S": return 16; // BC5 signed
                case "DX10":
                    throw new NotSupportedException("DX10 DDS header not supported in this tool.");
                case "A2XY": // Some Crytek games use this for BC5
                    Console.WriteLine("[Warning] Format A2XY is untested. Results may be incorrect.");
                    return 16;
                case "RXGB": // DXT5 variant, 16 bytes
                    Console.WriteLine("[Warning] Format RXGB is untested. Results may be incorrect.");
                    return 16;
                case "UYVY": // 16 bytes per 4x4 block (packed YUV 4:2:2)
                    Console.WriteLine("[Warning] Format UYVY is untested. Results may be incorrect.");
                    return 16;
                case "YUY2": // 16 bytes per 4x4 block (packed YUV 4:2:2)
                    Console.WriteLine("[Warning] Format YUY2 is untested. Results may be incorrect.");
                    return 16;
                // Add more formats as needed
                default:
                    throw new NotSupportedException($"Unsupported DDS FourCC: {fourCC} (supported: DXT1, DXT3, DXT5, ATI2, BC4U, BC4S, BC5U, BC5S, RXGB, UYVY, YUY2, A2XY)");
            }
        }

        static int GetBlockWidth(int width)
        {
            return (width + 3) / 4;
        }
        static int GetBlockHeight(int height)
        {
            return (height + 3) / 4;
        }

        static void FixEndian(byte[] data)
        {
            // For DXT1/3/5, 4x4 blocks are stored in 8 or 16 byte blocks
            // Swap every 2 bytes
            for (int i = 0; i < data.Length; i += 2)
            {
                byte tmp = data[i];
                data[i] = data[i + 1];
                data[i + 1] = tmp;
            }
        }

        static int Align(int value, int align)
        {
            return ((value + align - 1) / align) * align;
        }
		
		//I was going insane trying to unswizzle the texture 
		//I had to take a look at Xenia's gpu code to see what the hell was going on with the texture swizzling
        static int GetTiledOffset2D(int x, int y, int pitch, int bytesPerBlockLog2)
        {
            pitch = Align(pitch, 32);
            int macro = ((x >> 5) + (y >> 5) * (pitch >> 5)) << (bytesPerBlockLog2 + 7);
            int micro = ((x & 7) + ((y & 0xE) << 2)) << bytesPerBlockLog2;
            int offset = macro + ((micro & ~0xF) << 1) + (micro & 0xF) + ((y & 1) << 4);
            return ((offset & ~0x1FF) << 3) + ((y & 16) << 7) + ((offset & 0x1C0) << 2)
                + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6) + (offset & 0x3F);
        }

        static void UntileXbox360(byte[] tiled, byte[] linear, int blockWidth, int blockHeight, int bytesPerBlock)
        {
            int pitch = blockWidth;
            int bytesPerBlockLog2 = (int)Math.Log(bytesPerBlock, 2);

            for (int y = 0; y < blockHeight; y++)
            {
                for (int x = 0; x < blockWidth; x++)
                {
                    int tiledOffset = GetTiledOffset2D(x, y, pitch, bytesPerBlockLog2);
                    int linearOffset = (y * blockWidth + x) * bytesPerBlock;

                    if (tiledOffset + bytesPerBlock <= tiled.Length && linearOffset + bytesPerBlock <= linear.Length)
                    {
                        Array.Copy(tiled, tiledOffset, linear, linearOffset, bytesPerBlock);
                    }
                }
            }
        }

        static string GetRelativePath(string basePath, string path)
        {
            string absBase = Path.GetFullPath(basePath);
            string absPath = Path.GetFullPath(path);
            Uri baseUri = new Uri(AppendDirectorySeparatorChar(absBase));
            Uri pathUri = new Uri(absPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        static string AppendDirectorySeparatorChar(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
                return path + Path.DirectorySeparatorChar;
            return path;
        }
    }
}
