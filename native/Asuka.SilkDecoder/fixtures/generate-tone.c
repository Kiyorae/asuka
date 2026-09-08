/* Deterministic test data generated from a synthetic 400 Hz triangle wave.
 * This fixture generator and its output are original Asuka test material.
 * It has no microphone recordings or third-party audio content. */
#define _CRT_SECURE_NO_WARNINGS
#include <stdio.h>
#include <stdlib.h>
#include "SKP_Silk_SDK_API.h"

int main(int argc, char **argv)
{
    SKP_int32 size;
    SKP_SILK_SDK_EncControlStruct control = { 0 };
    SKP_int16 samples[480];
    unsigned char packet[1024];
    void *state;
    FILE *output;
    int frame, index;
    if (argc != 2 || SKP_Silk_SDK_Get_Encoder_Size(&size) != 0) return 1;
    state = calloc(1, size);
    if (!state || SKP_Silk_SDK_InitEncoder(state, &control) != 0) return 1;
    control.API_sampleRate = 24000;
    control.maxInternalSampleRate = 24000;
    control.packetSize = 480;
    control.bitRate = 24000;
    control.complexity = 2;
    output = fopen(argv[1], "wb");
    if (!output) return 1;
    fputc(2, output);
    fwrite("#!SILK_V3", 1, 9, output);
    for (frame = 0; frame < 10; frame++) {
        SKP_int16 count = sizeof(packet);
        for (index = 0; index < 480; index++) {
            int phase = index % 60;
            samples[index] = (SKP_int16)((phase < 30 ? phase : 60 - phase) * 500 - 7500);
        }
        if (SKP_Silk_SDK_Encode(state, &control, samples, 480, packet, &count) != 0 || count <= 0) return 1;
        fputc(count & 255, output);
        fputc((count >> 8) & 255, output);
        fwrite(packet, 1, count, output);
    }
    fclose(output);
    free(state);
    return 0;
}
