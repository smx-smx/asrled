/**
  * Copyright (C) 2023 Stefano Moioli <smxdev4@gmail.com>
  */
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ManagedHook
{

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AsrockMemOp
    {
        public uint mem_addr_hi;
        public uint mem_addr_lo;
        public uint mem_size;
        public uint transfer_size;
        public nint buffer_ptr;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AsrockPciOp
    {
        public byte bus;
        public byte dev;
        public ushort func;
        public uint offset;
        public uint result;
    }

    public enum AsrockOp : uint
    {
        MEM_READ = 0x222808,
        MEM_WRITE = 0x22280c,
        PCI_READ1 = 0x222830,
        PCI_READ4 = 0x222840,
        PCI_WRITE4 = 0x222838,
    }

    public enum PortID : byte {
        PSTH = 0x89,
        CSME0 = 0x90,
        GPIOCOM3 = 0xAC,
        GPIOCOM2 = 0xAD,
        GPIOCOM1 = 0xAE,
        GPIOCOM0 = 0xAF,
        PSF1 = 0xBA,
        SCS	 = 0xC0,
        RTC	 = 0xC3,
        ITSS = 0xC4,
        LPC	 = 0xC7,
        SERIALIO = 0xCB,
        DMI	 = 0xEF
    }

    public enum PchGpioPadID : ushort
    {
        PAD_CFG_DW0_GPP_D_1 = 0x4C8,
        PAD_CFG_DW0_GPP_H_15 = 0x7E0,
        PAD_CFG_DW0_GPP_H_16 = 0x7E8
    }

    public struct DeviceIoControlRequest
    {
        // timing
        public long ticks;

        public nint hDevice;
        public uint dwIoControlCode;
        public byte[] inData;
        public byte[] outData;
        public uint bytesReturned;
        public bool result;

        // $FIXME: should not be here
        public byte[] mem_buffer;
    }

    public record struct PchPadConfiguration
    {
        public bool GPIOTXSTATE;
        public bool GPIORXSTATE;
        public bool GPIOTXDIS;
        public bool GPIORXDIS;
        public byte PMODE;
        public bool GPIROUTNMI;
        public bool GPIROUTSMI;
        public bool GPIROUTSCI;
        public bool GPIROUTIOXAPIC;
        public bool RXINV;
        public byte RXEVCFG;
        public bool RXRAW1;
        public bool RXPADSTSEL;
        public byte PADRSTCFG;


        public static PchPadConfiguration Decode(uint value)
        {
            bool GPIOTXSTATE = ((value >> 0) & 1) == 1;
            bool GPIORXSTATE = ((value >> 1) & 1) == 1;
            bool GPIOTXDIS = ((value >> 8) & 1) == 1;
            bool GPIORXDIS = ((value >> 9) & 1) == 1;
            byte PMODE = ((byte)((value >> 10) & 0b111));
            bool GPIROUTNMI = ((value >> 17) & 1) == 1;
            bool GPIROUTSMI = ((value >> 18) & 1) == 1;
            bool GPIROUTSCI = ((value >> 19) & 1) == 1;
            bool GPIROUTIOXAPIC = ((value >> 20) & 1) == 1;
            bool RXINV = ((value >> 23) & 1) == 1;
            byte RXEVCFG = ((byte)((value >> 25) & 0b11));
            bool RXRAW1 = ((value >> 28) & 1) == 1;
            bool RXPADSTSEL = ((value >> 29) & 1) == 1;
            byte PADRSTCFG = ((byte)((value >> 30) & 0b11));

            return new PchPadConfiguration()
            {
                GPIOTXSTATE = GPIOTXSTATE,
                GPIORXDIS = GPIORXDIS,
                GPIORXSTATE = GPIORXSTATE,
                GPIOTXDIS = GPIOTXDIS,
                GPIROUTNMI = GPIROUTNMI,
                PMODE = PMODE,
                GPIROUTIOXAPIC = GPIROUTIOXAPIC,
                GPIROUTSCI = GPIROUTSCI,
                GPIROUTSMI = GPIROUTSCI,
                PADRSTCFG = PADRSTCFG,
                RXEVCFG = RXEVCFG,
                RXINV = RXINV,
                RXPADSTSEL= RXPADSTSEL,
                RXRAW1 = RXRAW1
            };
        }
    }

    public enum PchGpioPadDirection
    {
        NONE = 0,
        IN,
        OUT
    }

    public class PchGpioPadState
    {
        public PchGpioPadDirection direction;
        public bool value;
    }

    public interface ISigrokLog : IDisposable
    {
        public void Write(long time, byte b);
    }

    public class BinaryLogWriter : ISigrokLog
    {
        private BinaryWriter writer;
        
        public BinaryLogWriter(Stream output)
        {
            writer = new BinaryWriter(output);
        }

        public void Dispose()
        {
            writer.Flush();
            writer.Close();
            writer.Dispose();
        }

        public void Write(long time, byte b)
        {
            writer.Write(b);
        }
    }

    public class CsvLogWriter : ISigrokLog, IDisposable
    {
        private StreamWriter writer;
        private long referenceTime = Stopwatch.GetTimestamp();

        private readonly int num_fields;

        public CsvLogWriter(Stream output, IList<string> channels)
        {
            this.writer = new StreamWriter(output, new UTF8Encoding(false));

            var header = new string[channels.Count + 1];
            num_fields = header.Length;

            header[0] = "time";
            for(int i=0; i<channels.Count; i++)
            {
                header[1+i] = channels[i];
            }

            writer.WriteLine(string.Join(",", header));
        }

        public void Dispose()
        {
            writer.Flush();
            writer.Close();
            writer.Dispose();
        }

        public void Write(long time, byte b)
        {
            string[] line = new string[num_fields];
            
            double elapsed = ((double)time - referenceTime) / Stopwatch.Frequency;

            // 0.00000X
            line[0] = string.Format(CultureInfo.InvariantCulture, "{0:N6}", elapsed);

            for (int i=0; i<8; i++)
            {
                line[1 + i] = (((b >> i) & 1) == 1) ? "1" : "0";
            }

            writer.WriteLine(string.Join(",", line));
        }
    }

    public class AsrockDeviceIoControlHandler : IDisposable
    {
        private ISigrokLog log;
        private BlockingCollection<DeviceIoControlRequest> queue;
        private CancellationTokenSource cts;
        private Thread thread;

        private const bool BINARY_MODE = false;

        private const uint PCH_PCR_BASE_ADDRESS = 0xFD000000;

        private Dictionary<PchGpioPadID, PchGpioPadState> GpioPinStates = new Dictionary<PchGpioPadID, PchGpioPadState>()
        {
            // ICPRST
            { PchGpioPadID.PAD_CFG_DW0_GPP_D_1, new PchGpioPadState(){ direction = PchGpioPadDirection.IN, value = false } },
            // ICPCK
            { PchGpioPadID.PAD_CFG_DW0_GPP_H_15, new PchGpioPadState(){ direction = PchGpioPadDirection.IN, value = false } },
            // ICPDA
            { PchGpioPadID.PAD_CFG_DW0_GPP_H_16, new PchGpioPadState(){ direction = PchGpioPadDirection.IN, value = false } },
        };

        public AsrockDeviceIoControlHandler(string logFile)
        {
            var stream = new FileStream(logFile,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.Read);

            if (BINARY_MODE)
            {
                Console.WriteLine("= Using binary log writer");
                this.log = new BinaryLogWriter(stream);
            } else
            {
                Console.WriteLine("= Using csv log writer");
                this.log = new CsvLogWriter(stream, ImmutableArray.Create(
                    "ICPRST_W", "ICPCLK_W", "ICPDAT_W",
                    "ICPRST_R", "ICPCLK_R", "ICPDAT_R", "", ""
                ));
            }

            this.queue = new BlockingCollection<DeviceIoControlRequest>();
            this.cts = new CancellationTokenSource();
            this.thread = new Thread(StartInternal);
        }

        private static bool IsAsrockOp(uint ioctl)
        {
            return Enum.IsDefined(typeof(AsrockOp), ioctl);
        }

        public void EnqueueItem(DeviceIoControlRequest request)
        {
            if (IsAsrockOp(request.dwIoControlCode))
            {
                switch((AsrockOp)request.dwIoControlCode)
                {
                    case AsrockOp.MEM_WRITE:
                    case AsrockOp.MEM_READ:
                        // read the memory buffers.
                        // $FIXME: converting twice!
                        var memOp = ReadStruct<AsrockMemOp>(request.inData);
                        if(memOp.buffer_ptr != 0)
                        {
                            byte[] mem_data = new byte[memOp.transfer_size];
                            if(memOp.transfer_size > 0)
                            {
                                Marshal.Copy(memOp.buffer_ptr, mem_data, 0, mem_data.Length);
                                request.mem_buffer = mem_data;
                            }
                        }
                        break;
                }
            }
            queue.Add(request);
        }

        private static unsafe T ReadStruct<T>(byte[] data) where T : unmanaged
        {
            fixed (byte* pData = data) {
                return *(T *) pData;
            }
        }

        private void StartInternal()
        {
            try
            {
                var ienum = queue.GetConsumingEnumerable(cts.Token);
                foreach (var req in ienum)
                {
                    ProcessIoControl(req);
                }
            }
            catch (ThreadInterruptedException)
            {
                Console.Error.WriteLine("Stopping DeviceIoControl thread");
            }
        }

        public void Start()
        {
            thread.Start();
        }

        public void Stop()
        {
            queue.CompleteAdding();
            thread.Join();
        }

        private void ProcessPchAccess(AsrockMemOp memOp, DeviceIoControlRequest request)
        {
            ulong addr = (memOp.mem_addr_hi << 32) | memOp.mem_addr_lo;
            PortID pid = (PortID)(addr >> 16);
            ushort offset = (ushort)addr;

            var isRead = (AsrockOp)request.dwIoControlCode == AsrockOp.MEM_READ;

            switch (pid)
            {
                case PortID.GPIOCOM0:
                case PortID.GPIOCOM1:
                case PortID.GPIOCOM2:
                case PortID.GPIOCOM3:
                    switch ((PchGpioPadID)offset)
                    {
                        case PchGpioPadID.PAD_CFG_DW0_GPP_D_1:
                        case PchGpioPadID.PAD_CFG_DW0_GPP_H_15:
                        case PchGpioPadID.PAD_CFG_DW0_GPP_H_16:
                            var pad = (PchGpioPadID)offset;
                            uint reg = request.mem_buffer.Length switch
                            {
                                1 => request.mem_buffer[0],
                                2 => BinaryPrimitives.ReadUInt16LittleEndian(request.mem_buffer),
                                4 => BinaryPrimitives.ReadUInt32LittleEndian(request.mem_buffer),
                                _ => throw new NotImplementedException(),
                            };
                            var padState = PchPadConfiguration.Decode(reg);

                            var isSamplingRead = (isRead && !padState.GPIORXDIS);
                            var isSamplingWrite = (!isRead && !padState.GPIOTXDIS);

                            GpioPinStates[pad].direction =
                                (isSamplingWrite) ? PchGpioPadDirection.OUT
                                : (isSamplingRead) ? PchGpioPadDirection.IN
                                : PchGpioPadDirection.NONE;

                            var RxValue = (bool v) =>
                            {
                                if (padState.RXINV) return !v;
                                return v;
                            };

                            GpioPinStates[pad].value =
                                (isSamplingWrite) ? padState.GPIOTXSTATE
                                : (isSamplingRead) ? RxValue((padState.RXRAW1) ? true : padState.GPIORXSTATE)
                                : false;

                            var b = (byte)GpioPinStates.Select((p, i) => {
                                /** use a separate group to encode reads and writes **/
                                var shift = (p.Value.direction == PchGpioPadDirection.OUT)
                                    ? i
                                    : (i + GpioPinStates.Count);
                                return ((p.Value.value ? 1 : 0) & 1) << shift;
                            }).Sum();
                            log.Write(request.ticks, b);

                            /*var s_name = Enum.GetName(pad);
                            var s_inout = padState.GPIORXDIS ? "Output" : "Input";
                            var s_value = padState.GPIOTXSTATE ? "1" : "0";
                            Console.WriteLine($"{s_name} {s_inout} {s_value}");*/


                            //Console.WriteLine(PchPadConfiguration.Decode(reg));
                            break;
                        default:
                            Console.Error.WriteLine($"Unk offset {offset:X}");
                            break;
                    }
                    break;
                default:
                    Console.Error.WriteLine("Skipping access to port " + Enum.GetName(pid));
                    break;
            }
            
        }

        private void ProcessMemOp(AsrockMemOp memOp, DeviceIoControlRequest request)
        {
            ulong addr = (memOp.mem_addr_hi << 32) | memOp.mem_addr_lo;
            //Console.WriteLine($"mem: 0x{addr:X8}, size: {memOp.mem_size}, tx: {memOp.transfer_size}");

            if(addr >= PCH_PCR_BASE_ADDRESS)
            {
                ProcessPchAccess(memOp, request);
            }
        }

        private void ProcessPciOp(AsrockPciOp pciOp, DeviceIoControlRequest request)
        {
            //Console.WriteLine($"pci: {pciOp.bus:X2}.{pciOp.dev:X2}:{pciOp.func:X} {pciOp.offset:X} == {pciOp.result:X}");
        }

        private void ProcessIoControl(DeviceIoControlRequest request)
        {
            switch ((AsrockOp)request.dwIoControlCode)
            {
                case AsrockOp.MEM_READ:
                case AsrockOp.MEM_WRITE:
                    var memOp = ReadStruct<AsrockMemOp>(request.inData);
                    ProcessMemOp(memOp, request);
                    break;
                case AsrockOp.PCI_READ1:
                case AsrockOp.PCI_READ4:
                case AsrockOp.PCI_WRITE4:
                    //var pciOp = ReadStruct<AsrockPciOp>(request.inData);
                    //ProcessPciOp(pciOp, request);
                    break;
                default:
                    Console.Error.WriteLine($"Unhandled request {request.dwIoControlCode:X}");
                    break;
            }
        }

        public void Dispose()
        {
            log.Dispose();
        }
    }
}
