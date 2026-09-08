/* Asuka's bounded SILK file adapter. The codec is the vendored Skype SDK. */
#define _CRT_SECURE_NO_WARNINGS
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <fcntl.h>
#include <io.h>
#include <wchar.h>
#include "SKP_Silk_SDK_API.h"

#define MAX_INPUT_BYTES (16 * 1024 * 1024)
#define MAX_SAMPLES (24000 * 600)
#define MAX_PACKET_BYTES 1024

static int decode(FILE *input)
{
    unsigned char header[9], packet[MAX_PACKET_BYTES];
    SKP_int16 samples[960];
    SKP_int32 state_size = 0;
    SKP_SILK_SDK_DecControlStruct control = { 0 };
    void *state = NULL;
    int first, low, high, packet_size, frame_count, result = 2;
    unsigned int total_samples = 0;
    long input_size;

    if (fseek(input, 0, SEEK_END) != 0 || (input_size = ftell(input)) < 0 ||
        input_size > MAX_INPUT_BYTES || fseek(input, 0, SEEK_SET) != 0) return 2;
    first = fgetc(input);
    /* Some QQ files wrap Tencent SILK in an AMR magic prefix. */
    if (first == '#') {
        if (fread(header, 1, 5, input) != 5) return 2;
        if (memcmp(header, "!AMR\n", 5) == 0) first = fgetc(input);
        else if (fseek(input, 0, SEEK_SET) != 0) return 2;
        else first = fgetc(input);
    }
    if (first != 2 && ungetc(first, input) == EOF) return 2;
    if (fread(header, 1, 9, input) != 9 || memcmp(header, "#!SILK_V3", 9) != 0) return 2;

    if (SKP_Silk_SDK_Get_Decoder_Size(&state_size) != 0 || state_size <= 0 || state_size > 1048576) return 2;
    state = calloc(1, state_size);
    if (!state || SKP_Silk_SDK_InitDecoder(state) != 0) goto cleanup;
    control.API_sampleRate = 24000;
    control.framesPerPacket = 1;

    while ((low = fgetc(input)) != EOF) {
        high = fgetc(input);
        if (high == EOF) goto cleanup;
        packet_size = low | (high << 8);
        if (packet_size == 65535) {
            if (fgetc(input) != EOF) goto cleanup;
            break;
        }
        if (packet_size <= 0 || packet_size > MAX_PACKET_BYTES ||
            fread(packet, 1, packet_size, input) != (size_t)packet_size) goto cleanup;
        frame_count = 0;
        do {
            SKP_int16 count = 960;
            if (++frame_count > SILK_MAX_FRAMES_PER_PACKET ||
                SKP_Silk_SDK_Decode(state, &control, 0, packet, packet_size, samples, &count) != 0 ||
                count <= 0 || count > 480 || total_samples + count > MAX_SAMPLES) goto cleanup;
            if (fwrite(samples, sizeof(SKP_int16), count, stdout) != (size_t)count) goto cleanup;
            total_samples += count;
        } while (control.moreInternalDecoderFrames);
    }
    if (!ferror(input) && total_samples > 0 && fflush(stdout) == 0) result = 0;
cleanup:
    free(state);
    return result;
}

int wmain(int argc, wchar_t **argv)
{
    FILE *input;
    int result;
    if (argc != 2) return 2;
    _setmode(_fileno(stdout), _O_BINARY);
    input = _wfopen(argv[1], L"rb");
    if (!input) return 2;
    result = decode(input);
    fclose(input);
    if (result != 0) fputs("Invalid or oversized SILK audio.\n", stderr);
    return result;
}
