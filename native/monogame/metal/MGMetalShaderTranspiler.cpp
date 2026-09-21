// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#include "MGMetalShaderTranspiler.h"

#include "spirv_msl.hpp"

bool MGMTL_SpirvToMsl(
    const uint32_t* code,
    size_t words,
    std::string& outMsl,
    std::string& outEntry,
    std::string* errorMessage)
{
    try
    {
        spirv_cross::CompilerMSL msl(code, words);

        spirv_cross::CompilerMSL::Options mslOpts;
        mslOpts.platform = spirv_cross::CompilerMSL::Options::macOS;
        mslOpts.set_msl_version(2, 0);
        msl.set_msl_options(mslOpts);

        spirv_cross::CompilerGLSL::Options common = msl.get_common_options();
        common.vertex.flip_vert_y = MG_MTL_FLIP_VERT_Y;
        msl.set_common_options(common);

        auto model = msl.get_execution_model();
        auto resources = msl.get_shader_resources();
        const int SlotOffset = 32;

        auto pin = [&](uint32_t id, uint32_t baseTypeId, int tex, int samp, int buf)
        {
            spirv_cross::MSLResourceBinding binding;
            binding.stage = model;
            binding.desc_set = msl.get_decoration(id, spv::DecorationDescriptorSet);
            binding.binding = msl.get_decoration(id, spv::DecorationBinding);
            binding.count = 1;
            binding.basetype = msl.get_type(baseTypeId).basetype;
            binding.msl_buffer = buf < 0 ? 0 : static_cast<uint32_t>(buf);
            binding.msl_texture = tex < 0 ? 0 : static_cast<uint32_t>(tex);
            binding.msl_sampler = samp < 0 ? 0 : static_cast<uint32_t>(samp);
            msl.add_msl_resource_binding(binding);
        };

        for (auto& resource : resources.uniform_buffers)
            pin(resource.id, resource.base_type_id, -1, -1, MG_MTL_CBUFFER_INDEX);
        // SPIRV-Cross keys by stage/set/binding, shared by paired images and samplers.
        for (auto& resource : resources.separate_images)
        {
            int slot = static_cast<int>(msl.get_decoration(resource.id, spv::DecorationBinding)) - SlotOffset;
            pin(resource.id, resource.base_type_id, slot, slot, -1);
        }
        for (auto& resource : resources.separate_samplers)
        {
            int slot = static_cast<int>(msl.get_decoration(resource.id, spv::DecorationBinding)) - SlotOffset;
            pin(resource.id, resource.base_type_id, slot, slot, -1);
        }
        for (auto& resource : resources.sampled_images)
        {
            int slot = static_cast<int>(msl.get_decoration(resource.id, spv::DecorationBinding)) - SlotOffset;
            pin(resource.id, resource.base_type_id, slot, slot, -1);
        }

        outMsl = msl.compile();
        for (auto& entryPoint : msl.get_entry_points_and_stages())
        {
            if (entryPoint.execution_model == model)
            {
                outEntry = msl.get_cleansed_entry_point_name(entryPoint.name, entryPoint.execution_model);
                break;
            }
        }
        return !outEntry.empty();
    }
    catch (const std::exception& exception)
    {
        if (errorMessage != nullptr)
            *errorMessage = exception.what();
        return false;
    }
}