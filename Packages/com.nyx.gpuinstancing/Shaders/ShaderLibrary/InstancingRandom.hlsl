#ifndef NYX_INSTANCING_RANDOM_INCLUDED
#define NYX_INSTANCING_RANDOM_INCLUDED

// Portage exact de Unity.Mathematics (math.hash, Random : xorshift 32 bits). Le GPU tire les mêmes nombres, dans le même
// ordre, que les anciens jobs Burst : le placement des scènes existantes ne bouge pas. Calculs en entiers 32 bits
// (débordement modulo 2^32, comme en C#) ; NyxNextFloat construit le flottant bit à bit : identique au CPU.

// = math.hash(uint2)
uint NyxHash2(uint2 v)
{
    return v.x * 0x4473BBB1u + v.y * 0xCBA11D5Fu + 0x685835CFu;
}

// = math.hash(uint3)
uint NyxHash3(uint3 v)
{
    return v.x * 0xCD266C89u + v.y * 0xF1852A33u + v.z * 0x77E35E77u + 0x863E3729u;
}

struct NyxRandom
{
    uint state;
};

uint NyxNextState(inout NyxRandom random)
{
    uint t = random.state;
    random.state ^= random.state << 13;
    random.state ^= random.state >> 17;
    random.state ^= random.state << 5;
    return t;
}

// = new Unity.Mathematics.Random(seed), seed non nul
NyxRandom NyxCreateRandom(uint seed)
{
    NyxRandom random;
    random.state = seed;
    NyxNextState(random);
    return random;
}

// = Random.NextFloat() : [0, 1)
float NyxNextFloat(inout NyxRandom random)
{
    return asfloat(0x3f800000u | (NyxNextState(random) >> 9)) - 1.0;
}

// 32 bits hauts du produit de deux entiers 32 bits (pas d'entier 64 bits en Shader Model 5)
uint NyxMulHigh(uint a, uint b)
{
    uint aLow = a & 0xFFFFu, aHigh = a >> 16;
    uint bLow = b & 0xFFFFu, bHigh = b >> 16;
    uint low = aLow * bLow;
    uint middle1 = aHigh * bLow;
    uint middle2 = aLow * bHigh;
    uint carry = ((low >> 16) + (middle1 & 0xFFFFu) + (middle2 & 0xFFFFu)) >> 16;
    return aHigh * bHigh + (middle1 >> 16) + (middle2 >> 16) + carry;
}

// = Random.NextInt(count) : [0, count)
uint NyxNextInt(inout NyxRandom random, uint count)
{
    return NyxMulHigh(NyxNextState(random), count);
}

#endif // NYX_INSTANCING_RANDOM_INCLUDED
