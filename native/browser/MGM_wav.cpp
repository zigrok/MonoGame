#include "api_MGM.h"
#include "MGM_common.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <vector>

namespace
{
uint16_t u16(const unsigned char* p) { return p[0] | (p[1] << 8); }
uint32_t u32(const unsigned char* p) { return u16(p) | (static_cast<uint32_t>(u16(p+2)) << 16); }

// PCM is read one quarter-second at a time; seeking never decodes or buffers the whole song.
struct WaveDecoder : MGM_AudioDecoder
{
    FILE* file=nullptr;
    uint32_t dataOffset=0, length=0, position=0, sampleRate=0, channels=0;
    std::vector<mgbyte> buffer;
    ~WaveDecoder() override { if(file)std::fclose(file); }
    void Initialize(const char* path,MGM_AudioDecoderInfo& info) override
    {
        file=std::fopen(path,"rb");if(!file)return;
        unsigned char header[16];
        if(std::fread(header,1,12,file)!=12)return;
        bool format=false;
        while(std::fread(header,1,8,file)==8)
        {
            uint32_t bytes=u32(header+4);
            long start=std::ftell(file);
            if(std::memcmp(header,"fmt ",4)==0)
            {
                if(bytes<16 || std::fread(header,1,16,file)!=16)return;
                channels=u16(header+2);sampleRate=u32(header+4);
                if(u16(header)!=1 || u16(header+14)!=16 || channels<1 || channels>2 ||
                    sampleRate<8000 || sampleRate>192000 || u16(header+12)!=channels*2)return;
                format=true;
            }
            else if(std::memcmp(header,"data",4)==0)
            {
                dataOffset=static_cast<uint32_t>(start);length=bytes;
            }
            if(bytes>0x7fffffffu || std::fseek(file,start+bytes+(bytes&1),SEEK_SET)!=0)return;
            if(format && dataOffset)break;
        }
        if(!format || !dataOffset || !length || length%(channels*2)!=0)return;
        if(std::fseek(file,0,SEEK_END)!=0 || static_cast<uint64_t>(std::ftell(file))<static_cast<uint64_t>(dataOffset)+length)return;
        info.samplerate=sampleRate;info.channels=channels;
        info.duration=static_cast<uint64_t>(length)*1000/(sampleRate*channels*2);
        buffer.resize((sampleRate/4)*channels*2);
        SetPosition(0);
    }
    void SetPosition(mgulong milliseconds) override
    {
        uint64_t frame=milliseconds*sampleRate/1000;
        position=static_cast<uint32_t>(std::min<uint64_t>(frame*channels*2,length));
        std::fseek(file,dataOffset+position,SEEK_SET);
    }
    bool Decode(mgbyte*& output,mguint& size) override
    {
        size=static_cast<mguint>(std::fread(buffer.data(),1,std::min<size_t>(buffer.size(),length-position),file));
        output=buffer.data();position+=size;
        return position>=length || size==0;
    }
};
}

MGM_AudioDecoder* MGM_AudioDecoder_TryCreate_Wav(const uint8_t* signature)
{
    if(std::memcmp(signature,"RIFF",4) || std::memcmp(signature+8,"WAVE",4))return nullptr;
    return new WaveDecoder();
}
