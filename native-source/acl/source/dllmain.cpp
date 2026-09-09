// MIT License
// Copyright (c) 2026 SaigyoujiYusora
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#include "dllmain.h"
#include <acl/core/compressed_tracks.h>
#include <acl/decompression/decompress.h>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>

namespace {
constexpr uint32_t max_buffer = 32u * 1024u * 1024u;
constexpr uint32_t max_values = 8u * 1024u * 1024u;

// Length-aware validation of the canonical v10 representations emitted by the
// pinned compressor. Unsupported layouts (metadata, external defaults, raw
// transform formats, databases) are rejected rather than handed to ACL.
struct stream_view {
    const uint8_t* bytes; uint64_t size; bool ok=true;
    bool range(uint64_t offset,uint64_t count) const { return offset<=size && count<=size-offset; }
    uint32_t u32(uint64_t offset) {
        uint32_t value=0;
        if(!range(offset,4)) {ok=false;return 0;}
        std::memcpy(&value,bytes+offset,4);return value;
    }
};
uint64_t aligned(uint64_t value,uint64_t alignment) {return (value+alignment-1)&~(alignment-1);}
bool canonical_payload(const void* bytes,uint32_t size,uint8_t type) {
    stream_view v{static_cast<const uint8_t*>(bytes),size};
    const uint32_t tracks=v.u32(16),samples=v.u32(20),flags=v.u32(28);
    if(!v.ok || tracks==0 || tracks>65535 || samples==0 || samples>1000000 || (flags&0x80000000u)) return false;
    if(type==0) {
        if((flags&~0x40000000u)!=0 || !v.range(32,20))return false;
        const uint32_t bits=v.u32(32),metadata=v.u32(36),constants=v.u32(40),ranges=v.u32(44),animated=v.u32(48);
        if(metadata!=20 || !v.range(32+uint64_t(metadata),tracks))return false;
        uint64_t constant_count=0,range_count=0,expected_bits=0;
        for(uint32_t i=0;i<tracks;++i) {
            const uint8_t rate=v.bytes[32+metadata+i];
            if(rate>24)return false;
            constant_count+=rate==0;range_count+=rate>0 && rate<24;
            expected_bits+=rate==24 ? 32 : rate;
        }
        if(bits!=expected_bits || constants!=aligned(uint64_t(metadata)+tracks,4)
            || ranges!=uint64_t(constants)+4*constant_count || animated!=uint64_t(ranges)+8*range_count)return false;
        const uint64_t end=32+uint64_t(animated)+(expected_bits*samples+7)/8;
        // These 15 bytes are part of the declared serialized format, not an
        // external red zone used in place of proving payload extents.
        return v.ok && end+15==size;
    }
    if(type!=12 || (flags&0x3FFFF800u)!=0 || !(flags&0x200u) || (flags&0x100u)
        || ((flags>>4)&15)!=3 || (flags&12)!=12 || !v.range(32,52))return false;
    uint32_t h[13];for(uint32_t i=0;i<13;++i)h[i]=v.u32(32+i*4);
    const uint32_t segments=h[0],variable=h[1],ar=h[2],at=h[3],as=h[4],cr=h[5],ct=h[6],cs=h[7];
    if(segments==0 || segments>samples || h[8]!=0xFFFFFFFFu)return false;
    const uint32_t groups=(flags&1) ? 3 : 2;
    if(groups==2 && (as!=0 || cs!=0))return false;
    const bool stripped=(flags&0x400u)!=0;
    const uint32_t stride=stripped ? 20 : 16;
    const uint64_t index_bytes=segments>1 ? uint64_t(segments+1)*4 : 0;
    const uint64_t type_bytes=((tracks+15)/16)*4;
    if(h[9]!=52+index_bytes || h[10]!=uint64_t(h[9])+uint64_t(segments)*stride
        || h[11]!=uint64_t(h[10])+type_bytes*groups
        || !v.range(32+uint64_t(h[9]),uint64_t(segments)*stride)
        || !v.range(32+uint64_t(h[10]),type_bytes*groups))return false;
    const uint32_t animated_counts[3]={ar,at,as},constant_counts[3]={cr,ct,cs};
    for(uint32_t group=0;group<groups;++group) {
        uint32_t animated_count=0,constant_count=0;
        for(uint32_t i=0;i<type_bytes*4;++i) {
            const uint32_t kind=(v.u32(32+uint64_t(h[10])+group*type_bytes+(i/16)*4)>>(30-(i%16)*2))&3;
            if(kind==3 || (i>=tracks && kind!=0))return false;
            animated_count+=kind==2;constant_count+=kind==1;
        }
        if(animated_count!=animated_counts[group] || constant_count!=constant_counts[group])return false;
    }
    const uint64_t padded_rotations=aligned(ar,4);
    if(variable!=padded_rotations+at+as || h[12]!=uint64_t(h[11])+uint64_t(cr+ct+cs)*12)return false;
    uint64_t cursor=uint64_t(h[12])+uint64_t(ar+at+as)*24;
    if(!v.range(32+uint64_t(h[11]),cursor-h[11]))return false;
    if(segments>1 && (v.u32(84)!=0 || v.u32(84+uint64_t(segments)*4)!=0xFFFFFFFFu))return false;
    for(uint32_t segment=0;segment<segments;++segment) {
        const uint32_t start=segments>1 ? v.u32(84+uint64_t(segment)*4) : 0;
        const uint32_t end=segment+1<segments ? v.u32(84+uint64_t(segment+1)*4) : samples;
        if(start>=end || end>samples)return false;
        if(segments>1) {
            // Prove the decompressor's four-entry approximate search locates
            // the containing segment before reaching beyond its sentinel.
            for(uint32_t frame : {start,end-1}) {
                const uint32_t approximate=frame/(samples/segments);
                const uint32_t search=approximate>0 ? approximate-1 : 0;
                if(search>segment+1 || segment+1>=uint64_t(search)+4)return false;
            }
        }
        const uint64_t header=32+uint64_t(h[9])+uint64_t(segment)*stride;
        const uint32_t pose_bits=v.u32(header),rotation_bits=v.u32(header+4),translation_bits=v.u32(header+8);
        if(v.u32(header+12)!=cursor || !v.range(32+cursor,variable))return false;
        uint64_t bit_counts[3]={0,0,0};
        for(uint32_t i=0;i<variable;++i) {
            const uint8_t rate=v.bytes[32+cursor+i];
            if(rate>23 && rate!=31)return false;
            if(i>=ar && i<padded_rotations) {if(rate!=0)return false;continue;}
            if(segments==1 && rate==0)return false; // Segment constants require segment ranges.
            const uint32_t group=i<padded_rotations ? 0 : i<padded_rotations+at ? 1 : 2;
            bit_counts[group]+=uint64_t(rate==31 ? 32 : rate)*3;
        }
        if(rotation_bits!=bit_counts[0] || translation_bits!=bit_counts[1]
            || pose_bits!=bit_counts[0]+bit_counts[1]+bit_counts[2])return false;
        uint32_t stored=end-start;
        if(stripped) {
            if(stored>32)return false;
            const uint32_t mask=v.u32(header+16),allowed=0xFFFFFFFFu<<(32-stored);
            if(!(mask&0x80000000u) || !(mask&(1u<<(32-stored))) || (mask&~allowed))return false;
            stored=0;for(uint32_t m=mask;m;m&=m-1)++stored;
        }
        const uint64_t old=cursor;
        cursor=aligned(aligned(cursor+variable,2)+(segments>1 ? uint64_t(variable)*6 : 0),4)+(uint64_t(pose_bits)*stored+7)/8;
        if(!v.range(32+old,cursor-old))return false;
    }
    return v.ok && 32+cursor+15==size;
}

bool overlaps(const void* input,uint32_t input_size,const void* output,uint64_t output_size) {
    if(input_size==0)return false;
    const uintptr_t a=reinterpret_cast<uintptr_t>(input),b=reinterpret_cast<uintptr_t>(output);
    if(a>UINTPTR_MAX-input_size || b>UINTPTR_MAX-output_size)return true;
    return a<b+output_size && b<a+input_size;
}

struct bounded_writer final : acl::track_writer {
    float* values;
    uint32_t capacity, transform_tracks, scalar_tracks;
    uint32_t sample = 0;
    bool valid = true;
    bounded_writer(float* output, uint32_t count, uint32_t transforms, uint32_t scalars)
        : values(output), capacity(count), transform_tracks(transforms), scalar_tracks(scalars) {}
    float* destination(uint32_t track, uint32_t component, uint32_t width, bool scalar) {
        if(track >= (scalar ? scalar_tracks : transform_tracks)) { valid=false; return nullptr; }
        const uint64_t stride=uint64_t(transform_tracks)*10u+scalar_tracks;
        const uint64_t offset=uint64_t(sample)*stride+(scalar ? uint64_t(transform_tracks)*10u+track : uint64_t(track)*10u+component);
        if(offset+width>capacity) { valid=false; return nullptr; }
        return values+offset;
    }
    void RTM_SIMD_CALL write_rotation(uint32_t track, rtm::quatf_arg0 value) {
        if(float* out=destination(track,0,4,false)) rtm::quat_store(value,out);
    }
    void RTM_SIMD_CALL write_translation(uint32_t track, rtm::vector4f_arg0 value) {
        if(float* out=destination(track,4,3,false)) rtm::vector_store3(value,out);
    }
    void RTM_SIMD_CALL write_scale(uint32_t track, rtm::vector4f_arg0 value) {
        if(float* out=destination(track,7,3,false)) rtm::vector_store3(value,out);
    }
    void RTM_SIMD_CALL write_float1(uint32_t track, rtm::scalarf_arg0 value) {
        if(float* out=destination(track,0,1,true)) rtm::vector_store1(value.value,out);
    }
};

const acl::compressed_tracks* validate_stream(const void* bytes, uint32_t size, acl::track_type8 type) {
    if(bytes==nullptr || size<32 || size>max_buffer || (reinterpret_cast<uintptr_t>(bytes)&15u)!=0) return nullptr;
    uint32_t declared; std::memcpy(&declared,bytes,sizeof(declared));
    if(declared!=size || !canonical_payload(bytes,size,static_cast<uint8_t>(type))) return nullptr;
    acl::error_result error;
    const auto* tracks=acl::make_compressed_tracks(bytes,&error);
    if(tracks==nullptr || !error.empty() || !tracks->is_valid(true).empty()) return nullptr;
    if(tracks->get_version()!=acl::compressed_tracks_version16::v02_01_00 || tracks->get_track_type()!=type
        || tracks->get_num_tracks()==0 || tracks->get_num_tracks()>65535
        || tracks->get_num_samples_per_track()==0 || tracks->get_num_samples_per_track()>1000000
        || !std::isnormal(tracks->get_sample_rate()) || tracks->get_sample_rate()<=0 || tracks->get_sample_rate()>1000) return nullptr;
    if(type==acl::track_type8::qvvf && (tracks->has_database() || !tracks->has_trivial_default_values())) return nullptr;
    const float step=rtm::scalar_reciprocal(tracks->get_sample_rate());
    if(!std::isfinite(step) || !std::isfinite(tracks->get_finite_duration()))return nullptr;
    return tracks;
}
uint32_t inclusive_samples(const acl::compressed_tracks& tracks) {
    return tracks.get_num_samples_per_track()+(tracks.get_looping_policy()==acl::sample_looping_policy::wrap ? 1u : 0u);
}
}

// Buffers and result storage belong to the managed worker. No combined-stream
// probing, output allocation, database context, or uninitialized seek is used.
AS_API(int) SoraDecodeStreams(const void* transform_data, uint32_t transform_size,
    const void* scalar_data, uint32_t scalar_size, uint32_t samples, float rate,
    float* values, uint32_t capacity) {
    try {
        if(values==nullptr || !std::isfinite(rate) || rate<=0 || rate>1000 || samples==0 || samples>1000001
            || capacity==0 || capacity>max_values || (transform_size==0 && scalar_size==0)) return 1;
        if(overlaps(transform_data,transform_size,values,uint64_t(capacity)*sizeof(float))
            || overlaps(scalar_data,scalar_size,values,uint64_t(capacity)*sizeof(float)))return 1;
        const auto* transforms=transform_size ? validate_stream(transform_data,transform_size,acl::track_type8::qvvf) : nullptr;
        const auto* scalars=scalar_size ? validate_stream(scalar_data,scalar_size,acl::track_type8::float1f) : nullptr;
        if((transform_size && !transforms) || (scalar_size && !scalars)) return 2;
        const uint32_t transform_tracks=transforms ? transforms->get_num_tracks() : 0;
        const uint32_t scalar_tracks=scalars ? scalars->get_num_tracks() : 0;
        if((uint64_t(transform_tracks)*10u+scalar_tracks)*samples!=capacity) return 3;
        if((transforms && (transforms->get_sample_rate()!=rate || inclusive_samples(*transforms)!=samples))
            || (scalars && (scalars->get_sample_rate()!=rate || inclusive_samples(*scalars)!=samples))) return 4;
        acl::decompression_context<acl::default_transform_decompression_settings> transform_context;
        acl::decompression_context<acl::default_scalar_decompression_settings> scalar_context;
        if(transforms && (!transform_context.initialize(*transforms) || !transform_context.is_initialized())) return 5;
        if(scalars && (!scalar_context.initialize(*scalars) || !scalar_context.is_initialized())) return 5;
        for(uint32_t index=0;index<capacity;++index) values[index]=std::numeric_limits<float>::quiet_NaN();
        bounded_writer writer(values,capacity,transform_tracks,scalar_tracks);
        const float step=rtm::scalar_reciprocal(rate);
        for(uint32_t sample=0;sample<samples;++sample) {
            writer.sample=sample;
            const float time=sample*step;
            if(transforms) { transform_context.seek(time,acl::sample_rounding_policy::none); transform_context.decompress_tracks(writer); }
            if(scalars) { scalar_context.seek(time,acl::sample_rounding_policy::none); scalar_context.decompress_tracks(writer); }
            if(!writer.valid) return 6;
        }
        for(uint32_t index=0;index<capacity;++index) if(!std::isfinite(values[index])) return 7;
        return 0;
    } catch(...) { return 8; }
}
