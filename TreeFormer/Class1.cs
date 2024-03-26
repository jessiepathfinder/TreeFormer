using System.Collections;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using Intrin = System.Runtime.Intrinsics.X86;

namespace TreeFormer
{
	public readonly struct State{
		public readonly ReadOnlyMemory<ushort> prevs;
		public readonly ushort trueClass;
		public readonly int lookback;

		public State(ReadOnlyMemory<ushort> prevs, ushort trueClass, int lookback)
		{
			this.prevs = prevs;
			this.trueClass = trueClass;
			this.lookback = lookback;
		}
	}
	public sealed class DefaultLogDrain : ILogDrain
	{
		private DefaultLogDrain() { }
		public static readonly DefaultLogDrain instance = new DefaultLogDrain();
		public void Write(string str)
		{
			Console.WriteLine(str);
		}
	}
	public sealed class ThreadPrefixedLogDrain : ILogDrain
	{
		private ThreadPrefixedLogDrain() { }
		public static readonly ThreadPrefixedLogDrain instance = new ThreadPrefixedLogDrain();
		[ThreadStatic]
		public static string? threadPrefix;
		public void Write(string str)
		{
			Console.WriteLine((threadPrefix ?? Thread.CurrentThread.Name) + ": " + str);
		}
	}
	public interface ILogDrain{
		public void Write(string str);
	}
	public sealed class StateComparer : IEqualityComparer<State>, IComparer<State>{
		private readonly Vector128<byte> key;
		public StateComparer()
		{
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(new Span<Vector128<byte>>(ref key)));
		}

		public StateComparer(Vector128<byte> key)
		{
			this.key = key;
		}

		public int Compare(State x, State y)
		{
			return GetHashCode64(x).CompareTo(GetHashCode64(y));
		}

		public bool Equals(State x, State y)
		{
			return x.trueClass == y.trueClass & x.prevs.Equals(y.prevs);
		}

		public int GetHashCode(State obj)
		{
			Vector128<int> v128 = Intrin.Aes.Encrypt(key, Vector128.Create(obj.lookback,obj.trueClass, obj.prevs.GetHashCode(), obj.prevs.Length).AsByte()).AsInt32();
			return v128[0] ^ v128[1] ^ v128[2] ^ v128[3];
		}
		public ulong GetHashCode64(State obj)
		{
			Vector128<ulong> v128 = Intrin.Aes.Encrypt(key, Vector128.Create(obj.lookback, obj.trueClass, obj.prevs.GetHashCode(), obj.prevs.Length).AsByte()).AsUInt64();
			return v128[0] ^ v128[1];
		}
	}
	public sealed class UniqueEnumerator<T> : Dictionary<T, bool>, IEnumerator<T> where T : notnull
	{
		private readonly IEnumerator<T> underlying;

		public UniqueEnumerator(IEnumerator<T> underlying)
		{
			this.underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
		}

		public T Current => underlying.Current;

		object IEnumerator.Current => ((IEnumerator)underlying).Current;

		public void Dispose()
		{
			underlying.Dispose();
			Clear();
		}

		public bool MoveNext()
		{
			IEnumerator<T> underlying = this.underlying;
			while (underlying.MoveNext())
			{
				if (TryAdd(underlying.Current, false))
				{
					return true;
				}
			}
			return false;
		}

		public void Reset()
		{
			underlying.Reset();
			Clear();
		}
	}
	public sealed class UniqueEnumerable<T> : IEnumerable<T> where T : notnull
	{

		private readonly IEnumerable<T> underlying;

		public UniqueEnumerable(IEnumerable<T> underlying)
		{
			this.underlying = underlying ?? throw new ArgumentNullException(nameof(underlying));
		}

		public IEnumerator<T> GetEnumerator()
		{
			return new UniqueEnumerator<T>(underlying.GetEnumerator());
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return new UniqueEnumerator<T>(underlying.GetEnumerator());
		}
	}
	public static class Misc{
		public static int GetHashCode2(object? obj, int default_hash){
			return (obj is null) ? default_hash : obj.GetHashCode();
		}
		public static bool Equals2(object? a, object? b){
			return (a is null) ? b is null : a.Equals(b);
		}
		public static Dictionary<string, OptimizedTokenizerEntry> OptimizeDictionary(IReadOnlyDictionary<string, ushort> input)
		{
			string[] keys = input.Keys.ToArray();
			int len = keys.Length;
			Dictionary<string, OptimizedTokenizerEntry> thedict = new Dictionary<string, OptimizedTokenizerEntry>(len);

			foreach (KeyValuePair<string, ushort> kvp in input)
			{
				bool fastret = true;
				string str = kvp.Key;

				for (int i = 0, sl = str.Length; i < len;)
				{
					string str2 = keys[i++];
					if (str2.Length > sl && str2.StartsWith(str))
					{
						fastret = false;
						break;
					}
				}
				thedict.Add(str, new OptimizedTokenizerEntry(kvp.Value, fastret));
			}
			return thedict;
		}
		public static IEnumerable<State> RandomSelect(ushort[][] ushorts){
			int size = 0;
			int sle = ushorts.Length;
			for(int i = 0; i < sle; ++i){
				size += ushorts[sle].Length - 1;
			}
			Dictionary<State,bool> keyValuePairs = new Dictionary<State, bool>(comparer: new StateComparer());
			while(keyValuePairs.Count < size){
				ushort[] u2 = ushorts[RandomNumberGenerator.GetInt32(0, sle)];
				int offset = RandomNumberGenerator.GetInt32(0, u2.Length - 1);
				State state = new State(((ReadOnlyMemory<ushort>)u2).Slice(0, offset),u2[offset + 1],0);
				if(keyValuePairs.TryAdd(state, false)){
					yield return state;
				}
			}
		}

		public static int Tokenize(IReadOnlyDictionary<string, OptimizedTokenizerEntry> dict, Span<ushort> output, ReadOnlySpan<char> str, int maxtokensize, int specialTokenClasses)
		{
			if (maxtokensize < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(maxtokensize));
			}
			int pos = 0;
			int ctr2 = 0;
			for (int len = str.Length, outlen = output.Length; ctr2 < len & pos < outlen;)
			{
				StringBuilder sb = new StringBuilder();
				int token = -1;
				for (int i = ctr2++, stop = Math.Min(i + maxtokensize, len); i < stop; ++i)
				{
					sb.Append(str[i]);
					if (dict.TryGetValue(sb.ToString(), out OptimizedTokenizerEntry val))
					{
						token = val.value;
						ctr2 = i + 1;
						if (val.fastret)
						{
							break;
						}
					}
				}
				if (token > -1)
				{
					output[pos++] = (ushort)(token + specialTokenClasses);
				}
			}
			return pos;
		}
		public static IEnumerable<Node> Flatten(Node node)
		{
			Queue<Node>? queue1 = null;
			Node mynode = node;
		start:
			yield return mynode;
			Node? temp1 = mynode.child_false;
			Node? temp2 = mynode.child_true;
			if (temp1 is { })
			{
				mynode = temp1;
				if (temp2 is { })
				{
					queue1 ??= new Queue<Node>();
					queue1.Enqueue(temp2);
				}
				goto start;
			}
			if (temp2 is { })
			{
				mynode = temp2;
				goto start;
			}
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
			if (queue1 is { } && queue1.TryDequeue(out mynode))
			{
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
				goto start;
			}
		}
		public static void FixTree(IEnumerable<Node> nodes)
		{
			foreach (Node node in nodes)
			{
				FixNode(node, node.child_false, false);
				FixNode(node, node.child_true, true);
			}
		}
		public static void FixNode(Node parent, Node? child, bool mode)
		{
			if (child is { })
			{
				child.parent_mode = mode;
				child.parent = parent;
			}
		}
	}
	public readonly struct OptimizedTokenizerEntry
	{
		public readonly ushort value;
		public readonly bool fastret;

		public OptimizedTokenizerEntry(ushort value, bool fastret)
		{
			this.value = value;
			this.fastret = fastret;
		}
	}
}