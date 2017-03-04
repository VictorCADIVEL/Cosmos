﻿using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Schema;
using Cosmos.Assembler;
using Cosmos.Assembler.x86;
using Cosmos.Core.PCInformation;
using Cosmos.Debug.Kernel;
using Cosmos.IL2CPU.Plugs;

using XSharp.Compiler;

namespace Cosmos.Core.Plugs
{
    [Plug(Target = typeof(ProcessorInformation))]
    public unsafe class ProcessorInformationImpl
    {
        /* The following three int*-pointers are needed for the lea instruction due to the following reason:
         *      When comiling, the IL-code will be translated into x86-ASM, which has specific and unique names for local variables.
         *      To access these local variables, I have to pass their excat name to the instruction in question. This is rather
         *      difficult with reflection, if these variables reside in the local function scope. For this reason, I move the
         *      pointer to class scope to access them quicker and more easily
         */
        private static int* __cyclesrdtscptr, __raterdmsrptr, __vendortargetptr;
        private static long __ticktate = -1;

        /// <summary>
        /// Returns the number of CPU cycles since startup
        /// </summary>
        /// <returns>Number of CPU cycles</returns>
        public static long GetCycleCount()
        {
            int[] val = new int[2];

            fixed (int* ptr = val)
                __cyclesrdtsc(ptr);

            return ((long)val[0] << 32) | (uint)val[1];
        }

        /// <summary>
        /// Returns the CPU cycle rate (in cycles/µs)
        /// </summary>
        /// <returns>CPU cycle rate</returns>
        public static long GetCycleRate()
        {
            if (__ticktate == -1)
            {
                int[] raw = new int[4];

                fixed (int* ptr = raw)
                    __raterdmsr(ptr);

                ulong l1 = (ulong)__maxrate();
                ulong l2 = ((ulong)raw[0] << 32) | (uint)raw[1];
                ulong l3 = ((ulong)raw[2] << 32) | (uint)raw[3];

                __ticktate = (long)l2; // (long)((double)l1 * l3 / l2);
            }

            return __ticktate;
        }

        /// <summary>
        /// Copies the maximum cpu rate set by the bios at startup to the given int pointer
        /// </summary>
        [Inline]
        private static int __maxrate()
        {
            /*
             * mov eax, 16h
             * cpuid
             * and eax, ffffh
             * ret
             */
            
            XS.Set(XSRegisters.EAX, 0x00000016);
            XS.Cpuid();
            XS.And(XSRegisters.EAX, 0x0000ffff);
            XS.Return();

            return 0;
        }

        /// <summary>
        /// Copies the cycle count to the given int pointer
        /// </summary>
        [Inline]
        private static void __cyclesrdtsc(int* target)
        {
            /*
             * push eax
             * push ecx
             * push edx
             * lea esi, target
             * rdtsc
             * mov [esi+4], eax
             * mov [esi], edx
             * pop edx
             * pop ecx
             * pop eax
             * ret
             */
            __cyclesrdtscptr = target;

            string intname = LabelName.GetFullName(typeof(CPUImpl).GetField(nameof(__cyclesrdtscptr)));
            XS.Push(XSRegisters.EAX);
            XS.Push(XSRegisters.ECX);
            XS.Push(XSRegisters.EDX);
            XS.Lea(XSRegisters.ESI, intname);
            XS.Rdtsc();
            XS.Set(XSRegisters.ESI, XSRegisters.EAX, destinationIsIndirect: true, destinationDisplacement: 4);
            XS.Set(XSRegisters.ESI, XSRegisters.EDX, destinationIsIndirect: true);
            XS.Push(XSRegisters.EDX);
            XS.Push(XSRegisters.ECX);
            XS.Push(XSRegisters.EAX);
            XS.Return();
        }

        /// <summary>
        /// Copies the cycle rate to the given int pointer
        /// </summary>
        [Inline]
        private static void __raterdmsr(int* target)
        {
            /*
             * ; esi register layout: (mperf_hi, mperf_lo, aperf_hi, aperf_lo)
             * ;
             * ; int* ptr = new int[4];
             * ;
             * lea esi,        ptr  ;equivalent with `mov esi, &ptr`
             * mov ecx,        e7h
             * rdmsr
             * mov [esi + 4],  eax
             * mov [esi],      edx
             * mov ecx,        e8h
             * rdmsr
             * mov [esi + 12], eax
             * mov [esi + 8],  edx
             * xor eax,        eax
             * ret
             */
            __raterdmsrptr = target;

            string intname = LabelName.GetFullName(typeof(CPUImpl).GetField(nameof(__raterdmsrptr)));

            XS.Lea(XSRegisters.ESI, intname);
            XS.Set(XSRegisters.ECX, 0xe7);
            XS.Rdmsr();
            XS.Set(XSRegisters.EAX, XSRegisters.ESI, destinationIsIndirect: true, destinationDisplacement: 4);
            XS.Set(XSRegisters.EDX, XSRegisters.ESI, destinationIsIndirect: true, destinationDisplacement: 0);
            XS.Set(XSRegisters.ECX, 0xe8);
            XS.Rdmsr();
            XS.Set(XSRegisters.EAX, XSRegisters.ESI, destinationIsIndirect: true, destinationDisplacement: 12);
            XS.Set(XSRegisters.EDX, XSRegisters.ESI, destinationIsIndirect: true, destinationDisplacement: 8);
            XS.Xor(XSRegisters.EAX, XSRegisters.EAX); // XS.Set(XSRegisters.EAX, 0);
            XS.Return();
        }

        /// <summary>
        /// This function queries cpuid to get the registers involved.
        /// If a value is not used it will contain garbage.
        /// Requires that none of the arguments are null. THIS IS PROGRAMMER RESPONSABILITY
        /// </summary>
        /// call example <c>CPUID(0, &eax, &ebx, &ecx, &edx);</c> where eax, ebx, and edx are UINT
        /// <param name="eaxOperation">Number of the operation that cpuid will do.</param>
        /// <param name="eax">returned eax register (not null)</param>
        /// <param name="ebx">returned ebx register (not null)</param>
        /// <param name="ecx">returned ecx register (not null)</param>
        /// <param name="edx">returned edx register (not null)</param>
        [Inline]
        public static void CPUID(uint eaxOperation, uint* eax, uint* ebx, uint* ecx, uint* edx)
        {
            //Note that the arguments are pushed left to right. Thus, eaxoperation will be on the bottom of the stack.
            //Since the stack grows to 0, we need to put 24 to get the first arg.
            XS.Set(XSRegisters.EAX, XSRegisters.EBP, sourceDisplacement: 24);
            //Call cpuid to get the information
            XS.Cpuid();

            //Now comes a trick to only use 4 general purpose registers (eax, ebx, ecx, edx)

            //Save the possible value ebx contains
            XS.Push(XSRegisters.EBX);
            //Set in ebx a pointer to the data (in this case, a pointer to "uint* eax", i.e, the second argument)
            XS.Set(XSRegisters.EBX, XSRegisters.EBP, sourceDisplacement: (20));
            //Exchange the eax and ebx registers.
            //Now eax has the pointer and ebx has the value returned by cpuid in eax
            XS.Exchange(XSRegisters.EAX, XSRegisters.EBX); 
            //Store the cpuid eax value in uint *eax, i.e, store the cpuid eax in the second argument
            XS.Set(XSRegisters.EAX, XSRegisters.EBX, destinationIsIndirect: true);
            //Set ebx as it were
            XS.Pop(XSRegisters.EBX);

            //Do the same strategy for all the rest
            XS.Push(XSRegisters.EAX);
            XS.Set(XSRegisters.EAX, XSRegisters.EBP, sourceDisplacement: (16));
            XS.Exchange(XSRegisters.EBX, XSRegisters.EAX); 
            XS.Set(XSRegisters.EBX, XSRegisters.EAX, destinationIsIndirect: true);
            XS.Pop(XSRegisters.EAX);

            XS.Push(XSRegisters.EAX);
            XS.Set(XSRegisters.EAX, XSRegisters.EBP, sourceDisplacement: 12);
            XS.Exchange(XSRegisters.ECX, XSRegisters.EAX);
            XS.Set(XSRegisters.ECX, XSRegisters.EAX, destinationIsIndirect: true);
            XS.Pop(XSRegisters.EAX);

            XS.Push(XSRegisters.EAX);
            XS.Set(XSRegisters.EAX, XSRegisters.EBP, sourceDisplacement: 8);
            XS.Exchange(XSRegisters.EDX, XSRegisters.EAX);
            XS.Set(XSRegisters.EDX, XSRegisters.EAX, destinationIsIndirect: true);
            XS.Pop(XSRegisters.EAX);
    }

        [Inline]
        public static int CanReadCPUID()
        {
            /*
             * pushfd
             * pushfd
             * xor dword [esp], 00200000h
             * popfd
             * pushfd
             * pop eax
             * xor eax, [esp]
             * and eax, 00200000h
             * ret
             */
            XS.Pushfd();
            XS.Pushfd();
            XS.Xor(XSRegisters.ESP, 0x00200000, destinationIsIndirect: true);
            XS.Popfd();
            XS.Pushfd();
            XS.Pop(XSRegisters.EAX);
            XS.Xor(XSRegisters.EAX, XSRegisters.ESP, destinationIsIndirect: true);
            XS.Popfd();
            XS.And(XSRegisters.EAX, 0x00200000);
            XS.Set(XSRegisters.EAX, 1);
            XS.Push(XSRegisters.EAX);

            return 0;
        }
    }
}