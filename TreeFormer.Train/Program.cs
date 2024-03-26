using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace TreeFormer.Train
{
	internal static class Program
	{
		//2 magic token types
		//[START_GPT], [END_GPT]
		private const int magicTokenClasses = 2;
		private const int minimumInputTokens = 3;
		private const int maxContextSize = 2048;
		private const ulong dataStride = 256;
		private const int ensembleSize = 32;
		private static void Main(string[] args)
		{
			string datadir = args[0];
			string save = args[1];
			if (!datadir.EndsWith(Path.DirectorySeparatorChar))
			{
				datadir += Path.DirectorySeparatorChar;
			}
			Console.WriteLine("Loading dictionary...");
			IReadOnlyDictionary<string, ushort>? dict = JsonConvert.DeserializeObject<IReadOnlyDictionary<string, ushort>>(File.ReadAllText(datadir + "encoder.json"));
			if (dict is null)
			{
				Console.WriteLine("Null encoder dictionary");
				return;
			}

			int maxlen = 0;
			int tokenclasses = 0;
			foreach (KeyValuePair<string, ushort> keyValuePair in dict)
			{
				maxlen = Math.Max(maxlen, keyValuePair.Key.Length);
				tokenclasses = Math.Max(keyValuePair.Value, tokenclasses);
			}
			//2 magic token types
			//[START_GPT], [END_GPT]
			tokenclasses += magicTokenClasses + 1;
			int tokenClasses2 = tokenclasses;
			Console.WriteLine("Optimizing dictionary...");
			IReadOnlyDictionary<string, OptimizedTokenizerEntry>? dict1 = Misc.OptimizeDictionary(dict);
			dict = null;



			Console.WriteLine("Loading ELI5 + WikiQA question answering dataset...");
			Queue<string> dataqueue = new Queue<string>();

			using (StreamReader reader = new StreamReader(new DeflateStream(new FileStream(datadir + "QuestionAnswering.jsonl.deflate", FileMode.Open, FileAccess.Read, FileShare.Read, 16777216, FileOptions.SequentialScan), CompressionMode.Decompress, false), Encoding.UTF8, false, 16777216, false))
			{
			read:
				string? line = reader.ReadLine();
				if (line is { })
				{
					dataqueue.Enqueue(line);
					goto read;
				}
			}
			string[]? questionanswering = dataqueue.ToArray();
			int wqlen = questionanswering.Length;



			Console.WriteLine("Starting dataset tokenizers...");

			ConcurrentBag<ushort[]>? alldata = new();
			//ConcurrentBag<int[]>? classcounters = new();
			int threads = Environment.ProcessorCount;
			int loadprogress = 0;
			Thread[] thrlist = new Thread[threads];
			string progresstail = new StringBuilder("/").Append(wqlen).Append(" question-answer pairs").ToString();


			for (int z = 0; z < threads; ++z)
			{
				int az = z;
				Thread thread = new Thread(() =>
				{
					int za = az;
					StringBuilder sb = new StringBuilder("Tokenized ");
					Span<ushort> encbuffer = stackalloc ushort[maxContextSize + 1];
					Span<ushort> encbuffer2 = encbuffer.Slice(1, maxContextSize);
					int mywqlen = wqlen;
					//int[] counter = new int[tokenClasses2];
					//classcounters.Add(counter);
					//int sa2 = suboptimalSkipInitialTokens + 2;

					while (true)
					{
						int a = Interlocked.Increment(ref loadprogress);
						if (a > mywqlen)
						{
							break;
						}
						a -= 1;
						string raw = questionanswering[a];
						string[]? pair = JsonConvert.DeserializeObject<string[]>(raw);
						if (pair is null)
						{
							continue;
						}


						int size1 = Misc.Tokenize(dict1, encbuffer2, pair[0], maxlen, magicTokenClasses);
						if (size1 == maxContextSize)
						{
							continue;
						}
						if (size1 < minimumInputTokens)
						{
							continue;
						}



						encbuffer2[size1++] = 0; //user-to-GPT context switch
						int encsize2 = size1;
						if (size1 == maxContextSize)
						{
							continue;
						}
						//int encsize2 = size1;
						int ctd = Misc.Tokenize(dict1, encbuffer2[size1..], pair[1], maxlen, magicTokenClasses);
						if (ctd < minimumInputTokens)
						{
							continue;
						}
						size1 += ctd;
						if (size1 < maxContextSize)
						{
							encbuffer2[size1++] = 1; //GPT-to-user context switch
						}

						encbuffer[0] = (ushort)(encsize2 + 1);


						alldata.Add(encbuffer[..(size1 + 1)].ToArray());


						if ((a & 4095) == 4095)
						{
							Console.WriteLine(sb.Append(a).Append(progresstail).ToString());
							sb.Remove(10, sb.Length - 10);
						}

					}

					
				});
				thread.Name = "Dataset tokenizer thread #" + z;
				thread.IsBackground = true;
				thrlist[z] = thread;
				thread.Start();
			}
			Queue<string> savequeue = new Queue<string>();
			long[] shape1 = new long[] { 1, -1 };

			Console.WriteLine("Waiting for dataset tokenization to complete...");
			foreach (Thread thr in thrlist)
			{
				thr.Join();
			}



			Console.WriteLine("Optimizing memory usage...");
			questionanswering = null;
			dict1 = null;
			ushort[][] tokenized = alldata.ToArray();


			int cr = 0;
			using JsonWriter jsonWriter = new JsonTextWriter(new StreamWriter(new FileStream(save, FileMode.Create | FileMode.Append, FileAccess.Write, FileShare.None, 16777216, FileOptions.SequentialScan), Encoding.UTF8, -1, false));
			jsonWriter.WriteStartArray();
			for(int z = 0; z < threads; ++z){
				Thread thread = new Thread(() => {
					ushort[][] mytkz = tokenized;
					JsonWriter myw = jsonWriter;
					JsonSerializer jsonSerializer = new JsonSerializer();
					jsonSerializer.MaxDepth = int.MaxValue;
					jsonSerializer.NullValueHandling = NullValueHandling.Ignore;
					while (true){
						int i = Interlocked.Increment(ref cr);
						if(i > ensembleSize){
							return;
						}
						--i;

						ThreadPrefixedLogDrain.threadPrefix = "Tree #" + i + " training thread";
						Node? node = Trainer.TrainLegacy(EnumerateStates2(mytkz, FastRNG_State.GetRandom()), EnumerateStates(mytkz), 3, 1, 256, 4096, 128, 8, 1048576, 65536, 32, 0.002, ThreadPrefixedLogDrain.instance);
						if(node is { }){
							lock(myw){
								jsonSerializer.Serialize(myw, node);
							}
						}
					}
				});
				thread.Name = "Training thread #" + z;
				thread.IsBackground = true;
				thrlist[z] = thread;
				thread.Start();
			}
			foreach (Thread thr in thrlist)
			{
				thr.Join();
			}
			jsonWriter.WriteEndArray();
			jsonWriter.Flush();


		}
		private static IEnumerable<State> EnumerateStates(ushort[][] data, FastRNG_State fastRNG)
		{
			int step = 0;
			for (int z = 0, stop2 = data.Length; z < stop2; ++z) {
				ushort[] state = data[z];
				for(int i = state[0], stop = state.Length - 2; true; ){
					if(step == 0){
						yield return new State(((ReadOnlyMemory<ushort>)state).Slice(1, i), state[i + 2], 0);
						step = (int)((fastRNG.v1.AsUInt64()[0] % dataStride) + 1);
						fastRNG = fastRNG.Step();
					}
					
					i += step;
					if(i >= stop){
						step = i - stop;
						break;
					}
					step = 0;
				}
			}
		}
		private static IEnumerable<State> EnumerateStates2(ushort[][] data, FastRNG_State fastRNG)
		{
			for (int z = 0, stop2 = data.Length; z < stop2; ++z)
			{
				fastRNG = fastRNG.Step();
				if(fastRNG.v1.AsUInt64()[0] % dataStride == 0){
					ushort[] state = data[z];
					for (int i = state[0], stop = state.Length - 2; i < stop; ++i)
					{
						yield return new State(((ReadOnlyMemory<ushort>)state).Slice(1, i), state[i + 2], 0);
					}
				}
				
			}
		}
		private static IEnumerable<State> EnumerateStates(ushort[][] data)
		{
			for (int z = 0, stop2 = data.Length; z < stop2;++z)
			{
				ushort[] state = data[z];
				for (int i = state[0], stop = state.Length - 2; i < stop; ++i)
				{
					yield return new State(((ReadOnlyMemory<ushort>)state).Slice(1, i), state[i + 2], 0);
				}
			}
		}
	}
}