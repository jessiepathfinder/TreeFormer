using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Intrin = System.Runtime.Intrinsics.X86;

namespace TreeFormer
{
	
	[JsonObject(MemberSerialization.OptIn)]
	public sealed class Node{
		private static readonly Vector128<byte> vector128;
		static Node(){
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(new Span<Vector128<byte>>(ref vector128)));
		}
		/// <summary>
		/// Allows other methods to associate arbitary data with the node
		/// </summary>
		public readonly Dictionary<object, object> extraData = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
		
		public Node? parent;
		public bool parent_mode;


		[JsonProperty]
		public Node? child_true;
		[JsonProperty]
		public Node? child_false;
		[JsonProperty]
		public int lookback;
		[JsonProperty]
		public ushort compare;
		[JsonProperty]
		public bool dynamic_lookback;
		[JsonProperty]
		public bool relative_lookback;
		[JsonProperty]
		public Dictionary<string, double>? classProbs_true;
		[JsonProperty]
		public Dictionary<string, double>? classProbs_false;
		public override bool Equals(object? obj)
		{
			Node? other = obj as Node;
			return (other is { }) && (lookback == other.lookback & compare == other.compare & dynamic_lookback == other.dynamic_lookback & relative_lookback == other.relative_lookback & Misc.Equals2(child_false, other.child_false) & Misc.Equals2(child_true, other.child_true));
        }
		[JsonProperty]
		public ulong json_label;
		public override int GetHashCode()
		{
			Vector128<byte> v = Intrin.Aes.Encrypt(Vector128.Create(lookback, compare, dynamic_lookback ? 0 : 1, relative_lookback ? 0 : 1).AsByte(),vector128);
			Vector128<int> x = v.AsInt32();
			x = Intrin.Aes.Encrypt(Vector128.Create(x[0], x[1], Misc.GetHashCode2(child_false, x[2]), Misc.GetHashCode2(child_true, x[3])).AsByte(), v).AsInt32();

			return x[0] ^ x[1] ^ x[2] ^ x[3];
		}

		public IEnumerable<(Node node, bool mode)> TraceBackward(bool mode)
		{
			Node node1 = this;
		start:
			yield return (node1, mode);
			Node? node2 = node1.parent;

			if (node2 is { })
			{
				mode = node1.parent_mode;
				node1 = node2;
				goto start;
			}
		}
		public bool EvaluateMeOnly(ref State state){
			int len = state.prevs.Length;
			
			
			bool lbm = dynamic_lookback; //dynamic/static lookback mode
			ReadOnlySpan<ushort> ros = state.prevs.Span;
			ushort comp = compare;
			if (lbm){
				//Dynamic relative lookback mode
				for(int i = len - 2; i >= 0; --i){
					if(ros[i] == comp){
						state = new State(state.prevs, state.trueClass, state.lookback + (len - (i + 1)));
						return true;
					}
				}
				return false;
			}
			int total_lookback = lookback;
			if(relative_lookback){ //Static relative lookback
				total_lookback += state.lookback;
				if (total_lookback < 0)
				{
					return false;
				}
			}
			int effective_lookback = (len - total_lookback) - 1;
			if((effective_lookback > -1) && ros[effective_lookback] == comp){
				state = new State(state.prevs, state.trueClass, total_lookback);
				return true;
			}
			return false;
		}
	}
}
