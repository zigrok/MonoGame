#include "spirv_glsl.hpp"
#include <fstream>
#include <iostream>
#include <vector>

static void number(std::ofstream& output, uint32_t value)
{
    for (int shift = 0; shift < 32; shift += 8)
        output.put(static_cast<char>(value >> shift));
}
static void text(std::ofstream& output, const std::string& value)
{
    number(output, static_cast<uint32_t>(value.size()));
    output.write(value.data(), value.size());
}

int main(int argc, char** argv)
{
    if (argc != 3)
        return 2;
    try
    {
        std::ifstream input(argv[1], std::ios::binary | std::ios::ate);
        auto length = input.tellg();
        if (length <= 0 || length % 4 != 0)
            throw std::runtime_error("Invalid SPIR-V input");
        std::vector<uint32_t> words(static_cast<size_t>(length) / 4);
        input.seekg(0);
        input.read(reinterpret_cast<char*>(words.data()), length);
        spirv_cross::CompilerGLSL compiler(words);
        bool vertex = compiler.get_execution_model() == spv::ExecutionModelVertex;
        auto resources = compiler.get_shader_resources();
        if (!resources.storage_buffers.empty() || !resources.storage_images.empty() ||
            resources.uniform_buffers.size() > 1)
            throw std::runtime_error("BrowserGL supports one uniform buffer per stage and no storage resources");
        auto options = compiler.get_common_options();
        options.version = 300;
        options.es = true;
        options.vertex.flip_vert_y = true;
        options.vertex.fixup_clipspace = true;
        options.fragment.default_float_precision = spirv_cross::CompilerGLSL::Options::Highp;
        compiler.set_common_options(options);
        for (auto& varying : vertex ? resources.stage_outputs : resources.stage_inputs)
        {
            compiler.set_name(varying.id, "mg_varying_" + std::to_string(compiler.get_decoration(varying.id, spv::DecorationLocation)));
            compiler.unset_decoration(varying.id, spv::DecorationLocation);
        }
        std::string blockName = vertex ? "MG_VertexGlobals" : "MG_PixelGlobals";
        for (auto& block : resources.uniform_buffers)
            compiler.set_name(block.base_type_id, blockName);
        compiler.build_combined_image_samplers();
        struct Sampler { uint32_t image, sampler; std::string name; };
        std::vector<Sampler> samplers;
        for (const auto& combined : compiler.get_combined_image_samplers())
        {
            auto image = compiler.get_decoration(combined.image_id, spv::DecorationBinding);
            auto sampler = compiler.get_decoration(combined.sampler_id, spv::DecorationBinding);
            if (image < 32 || image >= 48 || sampler < 32 || sampler >= 48)
                throw std::runtime_error("BrowserGL sampler slot outside supported range");
            std::string name = std::string(vertex ? "mg_vs_" : "mg_ps_") + std::to_string(samplers.size());
            compiler.set_name(combined.combined_id, name);
            samplers.push_back({ image - 32, sampler - 32, name });
        }
        auto source = compiler.compile();
        std::ofstream output(argv[2], std::ios::binary);
        number(output, 0x4c47474d); // MGGL
        number(output, 1);
        text(output, source);
        text(output, resources.uniform_buffers.empty() ? "" : blockName);
        number(output, static_cast<uint32_t>(samplers.size()));
        for (const auto& sampler : samplers)
        {
            number(output, sampler.image);
            number(output, sampler.sampler);
            text(output, sampler.name);
        }
        if (!output)
            throw std::runtime_error("Cannot write BrowserGL shader");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
