using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Intrin = System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Intrinsics;

namespace TreeFormer
{
	public readonly struct FastRNG_State{
		public readonly Vector128<byte> v1;
		public readonly Vector128<byte> v2;

		public FastRNG_State(Vector128<byte> v1, Vector128<byte> v2)
		{
			this.v1 = v1;
			this.v2 = v2;
		}

		private FastRNG_State(bool f){
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(new Span<Vector128<byte>>(ref v1)));
			RandomNumberGenerator.Fill(MemoryMarshal.AsBytes(new Span<Vector128<byte>>(ref v2)));
		}
		public static FastRNG_State GetRandom(){
			return new FastRNG_State(false);
		}

		public FastRNG_State Step(){
			return new FastRNG_State(Intrin.Aes.Encrypt(v1, v2), Intrin.Aes.Encrypt(v2, v1));
		}
	}
}
