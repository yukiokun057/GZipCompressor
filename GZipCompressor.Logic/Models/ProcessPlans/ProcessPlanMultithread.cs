﻿using GZipCompressor.Logic.Interfaces;
using GZipCompressor.Logic.Models.BlockingCollections;
using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace GZipCompressor.Logic.Models.ProcessPlans
{
    class ProcessPlanMultithread : ProcessPlanBase
    {
        private string m_inputFile;
        private string m_outputFile;
        private ICompressible m_compressor;
        // raw file parts queue
        protected FixedBlockingQueue<FilePart> m_rawBlockQueue;
        // compressed file parts dictionary
        protected FixedBlockingDictionary<long, FilePart> m_compressedBlockDictionary;
        private ThreadPool m_threadPool;
        private AutoResetEvent m_threadBouncer;

        private const int c_blockSize = 2 << 10; // 1mb

        public ProcessPlanMultithread(string inputFilePath, string outputFilePath, ICompressible compressor) {
            m_inputFile = inputFilePath;
            m_outputFile = outputFilePath;
            initializePlan();
        }

        public override void Compress() {
            initializeCompressing();

            Thread rawConsumer = new Thread(consumeRawFilePart);
            Thread compressedConsumer = new Thread(consumeCompressedFilePart);

            using (var reader = new FileStream(m_inputFile, FileMode.Open, FileAccess.Read, FileShare.None, c_blockSize)) {
                byte[] buffer = new byte[c_blockSize];
                int blockIndex = 0;
                int readByteCount = 0;
                while ((readByteCount = reader.Read(buffer, 0, c_blockSize)) > 0) {
                    FilePart filePart = new FilePart(buffer, blockIndex);
                    m_rawBlockQueue.Add(filePart);
                }
                blockIndex++;
            }
        }

        public override void Decompress() {
            
        }

        private void initializeCompressing() {
            m_threadPool = new ThreadPool();
            m_threadBouncer = new AutoResetEvent(true);
        }

        private void consumeRawFilePart() {
            m_threadBouncer.WaitOne();

            Action compressionJob = () => {
                var filePart = m_rawBlockQueue.Take();
                var compressedData = m_compressor.Compress(filePart.Data);
                var compressedFilePart = new FilePart(compressedData, filePart.Index);
                m_compressedBlockDictionary.Add(filePart.Index, compressedFilePart);
            };
            m_threadPool.QueueTask(compressionJob);

            m_threadBouncer.Set();
        }


        private void consumeCompressedFilePart() {
            m_threadBouncer.WaitOne();

            Action writeJob = () => {
                var filePart = m_compressedBlockDictionary.Take();
                var compressedData = m_compressor.Compress(filePart.Data);
                var compressedFilePart = new FilePart(compressedData, filePart.Index);
                m_compressedBlockDictionary.Add(filePart.Index, compressedFilePart);
            };
            m_threadPool.QueueTask(writeJob);

            m_threadBouncer.Set();
        }

        private void initializePlan() {
            // getting avalible memory
            var availablePhysicalMemory = new ComputerInfo().AvailablePhysicalMemory;
            var clrMemoryRestriction = 1UL << 31; // 2gb
            var avalibleMemory = Math.Min(availablePhysicalMemory, clrMemoryRestriction);

            // 0.9 here is a synthetic restriction for case when avalible physical memory is less then net restriction
            var appAvalibleMemory = (ulong) (0.9 * avalibleMemory);
            var blockCountOnTwoQueues = (int)appAvalibleMemory / c_blockSize;

            // trying 50-50
            var rawQueueSize = blockCountOnTwoQueues / 2;
            var compressedQueueSize = blockCountOnTwoQueues / 2;

            if (compressedQueueSize == 0 || rawQueueSize == 0)
                throw new InsufficientMemoryException("Not enough memory");

            m_rawBlockQueue = new FixedBlockingQueue<FilePart>(rawQueueSize);
            m_compressedBlockDictionary = new FixedBlockingDictionary<int, FilePart>(compressedQueueSize);
        }
    }
}