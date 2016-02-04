#region usings
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;

using LSL;

#endregion usings

// WARNING: 1s timeout for resolving stream, will freeze the program that much if could not find any corresponding stream type.

namespace VVVV.Nodes
{
	
	//ISpread <double>
	#region PluginInfo
	[PluginInfo(Name = "LSLReceiveData", AutoEvaluate = true, Category = "Value", Help = "Lab Streaming Layer receiving protocol", Tags = "lsl, network")]
	#endregion PluginInfo
	
	public class LSLReceiveDataNode : IPluginEvaluate, IPartImportsSatisfiedNotification
    {
		[Import()]
		ILogger Flogger;

        #region fields & pins
        // Enable
        [Input("Enabled", IsSingle = true, IsToggle = true)]
        public IDiffSpread<bool> FEnabled;

        // Manually ask for updating the streams
        [Input("Update Streams", IsSingle = true, IsBang = true)]
        public ISpread<bool> FUpdate;

        // how many seconds should be buffered by LSL; low value to ensure real-time, high to limit data loss. At least 1 second.
        [Input("MaxBufLen", DefaultValue = 1, IsSingle = true)]
		public ISpread<int> FMaxBufLen;
		
		// leave to 0 to avoid lag, even if signal may get distored
		[Input("TimeOut", DefaultValue = 0, IsSingle = true)]
		public ISpread<double> FTimeOut;
		
		// will fetch at least that much samples per loop. If too low may miss values, if too high and can't keep up pace with server, may slow down computer
		// Should be a multiple of ChunkSize to ensure that the actual number of samples read reamain below.
		[Input("MaxSamples", DefaultValue = 512, IsSingle = true)]
		public ISpread<int> FMaxSamples;
		
		// chunk size for each pull, too low may slow down computer, too high... ?
		[Input("ChunkSize", DefaultValue = 32, IsSingle = true)]
		public ISpread<int> FChunkSize;

        // Stream type
        [Input("StreamType", DefaultString = "type", IsSingle = true)]
        public IDiffSpread<string> FResourceType;

        //A spread of Stream names
        public Spread<IIOContainer<IDiffSpread<string>>> FResourceName = new Spread<IIOContainer<IDiffSpread<string>>>();

        //A config to specify the number of stream required.
        //@note: ideally, we would only take a spread of Stream Names as input and based on the number of slices, create the according number of output pins but
        //       I'm struggling to have a consistent behavior.
        [Config("Stream name count", DefaultValue = 1, MinValue = 0)]
        public IDiffSpread<int> FResourceNameCount;

        // Spreads of data (where bin size is the number of channels)
        public Spread<IIOContainer<ISpread<ISpread<double>>>> FData = new Spread<IIOContainer<ISpread<ISpread<double>>>>();

        //Sample rates
        public Spread<IIOContainer<ISpread<double>>> FSampleRate = new Spread<IIOContainer<ISpread<double>>>();

        [Import]
        public IIOFactory FIOFactory;
		#endregion fields & pins
		
        // The private stream informations
		private liblsl.StreamInfo[] mInfo;
		private liblsl.StreamInlet[] mInlet;
        private int[] mNbChannel;
        private double[] mSampleRate;

        public void OnImportsSatisfied()
        {
            //Register listeners
            FEnabled.Changed += FEnabled_Changed;
            FResourceType.Changed += FResourceType_Changed;
            FResourceNameCount.Changed += FResourceNameCount_Changed;
        }

        private void FEnabled_Changed(IDiffSpread<bool> spread)
        {
            if (spread[0])
                Connect();
        }

        private void FResourceNameCount_Changed(IDiffSpread<int> spread)
        {
            //Update the stream names input pins
            HandlePinCountChanged(
                spread,
                FResourceName,
                (i) =>
                {
                    var io = new InputAttribute(string.Format("Stream name {0}", i));
                    io.IsSingle = true;
                    return io;
                }
                );

            //Update the output pins
            HandlePinCountChanged(
                spread,
                FData,
                (i) =>
                {
                    var io = new OutputAttribute(string.Format("Data stream {0}", i));
                    return io;
                }
                );

            HandlePinCountChanged(
                spread,
                FSampleRate,
                (i) =>
                {
                    var io = new OutputAttribute(string.Format("Sample rate {0}", i));
                    return io;
                }
                );

            //Register events on newly created pins
            for (int i = 0; i < FResourceNameCount[0]; ++i)
            {
                FResourceName[i].IOObject.Changed += StreamName_Changed;
            }
        }

        private void StreamName_Changed(IDiffSpread<string> spread)
        {
            //If enabled, recreate connections right away
            if (FEnabled[0])
                Connect();
        }

        private void FResourceType_Changed(IDiffSpread<string> spread)
        {
            //If enabled, recreate connections right away
            if (FEnabled[0])
                Connect();
        }

        private void HandlePinCountChanged<T>(ISpread<int> countSpread, Spread<IIOContainer<T>> pinSpread, Func<int, IOAttribute> ioAttributeFactory) where T : class
        {
            pinSpread.ResizeAndDispose(
                countSpread[0],
                (i) =>
                {
                    var ioAttribute = ioAttributeFactory(i);
                    var io = FIOFactory.CreateIOContainer<T>(ioAttribute);
                    return io;
                }
            );
        }

        void Connect()
        {
            //Find all the streams of the specified type
            liblsl.StreamInfo[] streamInfo;
            streamInfo = liblsl.resolve_stream("type", FResourceType[0], 1, FTimeOut[0]);

            mInfo = new liblsl.StreamInfo[FResourceNameCount[0]];
            mInlet = new liblsl.StreamInlet[FResourceNameCount[0]];
            mNbChannel = new int[FResourceNameCount[0]];
            mSampleRate = new double[FResourceNameCount[0]];
            
            //For each stream name, search for it in the found streams
            for (int i = 0; i < FResourceNameCount[0]; ++i)
            {
                foreach (liblsl.StreamInfo info in streamInfo)
                {
                    if (info.name().Equals(FResourceName[i].IOObject[0]))
                    {
                        //Safe info of found stream
                        mInfo[i] = info;

                        //Create stream inlet
                        mInlet[i] = new liblsl.StreamInlet(info, FMaxBufLen[0]);

                        //Get info from the stream
                        mNbChannel[i] = info.channel_count();
                        mSampleRate[i] = info.nominal_srate();

                        ////Found, so skip to next stream
                        //break;
                    }
                }
            }
        }

        //called when data for any output pin is requested
        public void Evaluate(int SpreadMax)
		{
            //Check if manual synchronization of streams is requested
            if (FUpdate[0])
                Connect();

            //Retrieve the data from each stream
            if (FEnabled[0])
            {
                for (int pin = 0; pin < FResourceNameCount[0]; ++pin)
                {
                    //Only pull valid streams
                    if (mInlet[pin] != null)
                    {
                        //Pull everything we can
                        int totalChunks = 0;
                        double[,] sample = new double[FChunkSize[0], mNbChannel[pin]];
                        double[] timestamps = new double[FChunkSize[0]];
                        int nbChunks = -1;
                        List<List<double>> data = new List<List<double>>();
                        while (nbChunks != 0 && totalChunks < FMaxSamples[0])
                        {
                            //Pull the chunks
                            nbChunks = mInlet[pin].pull_chunk(sample, timestamps, FTimeOut[0]);
                            totalChunks += nbChunks;

                            //Queue the chunks
                            for (int i = 0; i < nbChunks; ++i)
                            {
                                List<double> singleSample = new List<double>();
                                for (int j = 0; j < mNbChannel[pin]; ++j)
                                {
                                    singleSample.Add(sample[i, j]);
                                }
                                data.Add(singleSample);
                            }

                            //Reverse order so that the most recent samples are first in the list
                            data.Reverse();
                        }

                        //Output on the pins
                        FData[pin].IOObject.SliceCount = data.Count;
                        for (int i = 0; i < data.Count; ++i)
                        {
                            FData[pin].IOObject[i].SliceCount = data[i].Count;
                            FData[pin].IOObject[i].AssignFrom(data[i]);
                        }
                    }
                }
            }
        }
    }
}
