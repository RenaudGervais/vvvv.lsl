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
	[PluginInfo(Name = "LSLReceiveData", Category = "Value", Help = "Lab Streaming Layer receiving protocol", Tags = "lsl, network")]
	#endregion PluginInfo
	
	public class LSLReceiveDataNode : IPluginEvaluate, IPartImportsSatisfiedNotification
    {
		[Import()]
		ILogger Flogger;

        #region fields & pins
        // Manually ask the node to resovle the stream
        [Input("Enabled", IsSingle = true, IsToggle = true)]
        public IDiffSpread<bool> FEnabled;

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
        public Spread<IIOContainer<ISpread<string>>> FResourceName = new Spread<IIOContainer<ISpread<string>>>();

        //A config to specify the number of stream required.
        //@note: ideally, we would only take a spread of Stream Names as input and based on the number of slices, create the according number of output pins but
        //       I'm struggling to have a consistent behavior.
        [Config("Stream name count", DefaultValue = 1, MinValue = 0)]
        public IDiffSpread<int> FResourceNameCount;

        // Spreads of data (where bin size is the number of channels)
        public Spread<IIOContainer<ISpread<ISpread<double>>>> FData = new Spread<IIOContainer<ISpread<ISpread<double>>>>();

        //Sample rates
        public Spread<IIOContainer<ISpread<double>>> FSampleRate = new Spread<IIOContainer<ISpread<double>>>();

		//[Output("SampleRate", IsSingle = true) ]
		//public ISpread<double> FSampleRate;

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
            throw new NotImplementedException();
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
        }

        private void FResourceType_Changed(IDiffSpread<string> spread)
        {
            throw new NotImplementedException();
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

                        //Found, so skip to next stream
                        break;
                    }
                }
            }


            //results = liblsl.resolve_stream("type", FResourceType[0], 1, 1);

            //Flogger.Log(LogType.Debug, "Number of streams: " + results.Length);

            //for (int i = 0; i < results.Length; i++)
            //{
            //    liblsl.StreamInlet tmpInlet = new liblsl.StreamInlet(results[i], FMaxBufLen[0]);
            //    liblsl.StreamInfo tmpInfo = tmpInlet.info();
            //    Flogger.Log(LogType.Debug, "Look at stream name: " + tmpInfo.name());
            //    if (FResourceName[0].Equals(tmpInfo.name()))
            //    {
            //        Flogger.Log(LogType.Debug, "Bingo!");
            //        // retrieve data
            //        mInfo = tmpInfo;
            //        mInlet = tmpInlet;
            //        FNBChannels[0] = mInfo.channel_count();
            //        FSampleRate[0] = mInfo.nominal_srate();
            //        Flogger.Log(LogType.Debug, "Nb channels: " + mInfo.channel_count());
            //        Flogger.Log(LogType.Debug, "Sample rate: " + mInfo.nominal_srate());
            //        break;
            //    }
            //}
        }

        //called when data for any output pin is requested
        public void Evaluate(int SpreadMax)
		{
    //        // Try to establish connexion when input stream name changed or if
    //        // manually triggered
    //        if (FEnabled[0])
    //            Connect();

    //        if (FNBChannels[0] > 0)
    //        {
				//// First slices: channels
    //    		FData.SliceCount = FNBChannels[0];
				
				//// VPRN tells us how many values we have and we know the size of a chunk: easy to compute how many channels we receive
    //   			//FNBChannels[0] = 1;// e.Channels.Length / FChunkSize[0];
        	
				//// pull all we can
				//int totalChunks = 0;
				//float[,] sample = new float[FChunkSize[0],FNBChannels[0]];
				//double[] timestamps = new double[FChunkSize[0]];
				//int nbChunks = -1;
				////Flogger.Log(LogType.Debug,"new loop ");
				//while(nbChunks != 0 && totalChunks < FMaxSamples[0] ) {
				//	nbChunks = inlet.pull_chunk( sample, timestamps, FTimeOut[0]);
				//	//Flogger.Log(LogType.Debug,"timestamp: " + nbChunks);
				//	totalChunks += nbChunks;
					
				//	// try to fill
				//	for (int chan = 0; chan < FNBChannels[0]; chan++) {
				//		// Within slices we have chunks
				//		FData[chan].SliceCount = totalChunks;
				//		for (int i = 0; i < nbChunks; i++) {
				//			// fill from the end
				//			FData[chan][totalChunks-nbChunks+i] = sample[i,chan];
				//		}
				//	}	
				//}
				
            	//for (int chan = 0; chan < FNBChannels[0]; chan++) {
            	//	// Within slices we have chunks
				//	FOutput[chan].SliceCount = FChunkSize;
            	//	for (int i = 0; i < FChunkSize; i++) {
            	//		// We move to the correct position
            	//		int pos = chan * FChunkSize + i;
            	//		FOutput[chan][i] = 1;//e.Channels[pos];
            	//	}
		   		//}
			//}
		}
    }
}
