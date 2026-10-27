using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>Six entries of ollama.com/library?sort=popular as served on 2026-10-27, icons removed (phase 119).</summary>
internal static class OllamaLibraryFixture
{
    public const string Page = """""
<li  class="flex items-baseline border-b border-neutral-200 py-6">
      <a href="/library/nomic-embed-text" class="group w-full space-y-5">
        <div  title="nomic-embed-text" class="flex flex-col">
          <h2 class="truncate text-xl font-medium underline-offset-2 md:text-2xl">
            <div class="flex space-x-2 items-center">
              <span class="group-hover:underline truncate">nomic-embed-text</span>
            </div>
          </h2>
          <p class="max-w-lg break-words text-neutral-800 text-md">A high-performing open embedding model with a large token context window.</p>
        </div>
        <div class="flex flex-col space-y-2">
          <div class="flex flex-wrap space-x-2">
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">embedding</span>
          </div>
          <p class="flex space-x-5 text-[13px] font-medium text-neutral-500">
              <span class="flex items-center">
                <span >93.7M</span>
                <span class="hidden sm:flex">&nbsp;Pulls</span>
              </span>
              <span class="flex items-center">
                <span >3</span>
                <span class="hidden sm:flex">&nbsp;Tags</span>
              </span>
              <span class="flex items-center" title="Feb 21, 2024 5:26 PM UTC">
                <span class="hidden sm:flex">Updated&nbsp;</span>
                <span >2 years ago</span>
              </span>
          </p>
        </div>
      </a>
    </li>
<li  class="flex items-baseline border-b border-neutral-200 py-6">
      <a href="/library/qwen2.5" class="group w-full space-y-5">
        <div  title="qwen2.5" class="flex flex-col">
          <h2 class="truncate text-xl font-medium underline-offset-2 md:text-2xl">
            <div class="flex space-x-2 items-center">
              <span class="group-hover:underline truncate">qwen2.5</span>
            </div>
          </h2>
          <p class="max-w-lg break-words text-neutral-800 text-md">Qwen2.5 models are pretrained on Alibaba&#39;s latest large-scale dataset, encompassing up to 18 trillion tokens. The model supports up to 128K tokens and has multilingual support. </p>
        </div>
        <div class="flex flex-col space-y-2">
          <div class="flex flex-wrap space-x-2">
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">tools</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">0.5b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">1.5b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">3b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">7b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">14b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">32b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">72b</span>
          </div>
          <p class="flex space-x-5 text-[13px] font-medium text-neutral-500">
              <span class="flex items-center">
                <span >43.7M</span>
                <span class="hidden sm:flex">&nbsp;Pulls</span>
              </span>
              <span class="flex items-center">
                <span >133</span>
                <span class="hidden sm:flex">&nbsp;Tags</span>
              </span>
              <span class="flex items-center" title="Sep 19, 2024 4:42 PM UTC">
                <span class="hidden sm:flex">Updated&nbsp;</span>
                <span >2 years ago</span>
              </span>
          </p>
        </div>
      </a>
    </li>
<li  class="flex items-baseline border-b border-neutral-200 py-6">
      <a href="/library/mixtral" class="group w-full space-y-5">
        <div  title="mixtral" class="flex flex-col">
          <h2 class="truncate text-xl font-medium underline-offset-2 md:text-2xl">
            <div class="flex space-x-2 items-center">
              <span class="group-hover:underline truncate">mixtral</span>
            </div>
          </h2>
          <p class="max-w-lg break-words text-neutral-800 text-md">A set of Mixture of Experts (MoE) model with open weights by Mistral AI in 8x7b and 8x22b parameter sizes.</p>
        </div>
        <div class="flex flex-col space-y-2">
          <div class="flex flex-wrap space-x-2">
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">tools</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">8x7b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">8x22b</span>
          </div>
          <p class="flex space-x-5 text-[13px] font-medium text-neutral-500">
              <span class="flex items-center">
                <span >2.9M</span>
                <span class="hidden sm:flex">&nbsp;Pulls</span>
              </span>
              <span class="flex items-center">
                <span >70</span>
                <span class="hidden sm:flex">&nbsp;Tags</span>
              </span>
              <span class="flex items-center" title="Dec 20, 2024 8:27 AM UTC">
                <span class="hidden sm:flex">Updated&nbsp;</span>
                <span >1 year ago</span>
              </span>
          </p>
        </div>
      </a>
    </li>
<li  class="flex items-baseline border-b border-neutral-200 py-6">
      <a href="/library/minimax-m2.7" class="group w-full space-y-5">
        <div  title="minimax-m2.7" class="flex flex-col">
          <h2 class="truncate text-xl font-medium underline-offset-2 md:text-2xl">
            <div class="flex space-x-2 items-center">
              <span class="group-hover:underline truncate">minimax-m2.7</span>
            </div>
          </h2>
          <p class="max-w-lg break-words text-neutral-800 text-md">MiniMax&#39;s M2-series model for coding, agentic workflows, and professional productivity.</p>
        </div>
        <div class="flex flex-col space-y-2">
          <div class="flex flex-wrap space-x-2">
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">tools</span>
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">thinking</span>
              <span class="inline-flex items-center rounded-md bg-cyan-50 px-2 py-0.5 text-xs font-medium text-cyan-500 sm:text-[13px]">cloud</span>
          </div>
          <p class="flex space-x-5 text-[13px] font-medium text-neutral-500">
              <span class="flex items-center">
                <span >2.4M</span>
                <span class="hidden sm:flex">&nbsp;Pulls</span>
              </span>
              <span class="flex items-center">
                <span >1</span>
                <span class="hidden sm:flex">&nbsp;Tag</span>
              </span>
              <span class="flex items-center" title="Mar 18, 2026 6:05 PM UTC">
                <span class="hidden sm:flex">Updated&nbsp;</span>
                <span >7 months ago</span>
              </span>
          </p>
        </div>
      </a>
    </li>
<li  class="flex items-baseline border-b border-neutral-200 py-6">
      <a href="/library/gemma3n" class="group w-full space-y-5">
        <div  title="gemma3n" class="flex flex-col">
          <h2 class="truncate text-xl font-medium underline-offset-2 md:text-2xl">
            <div class="flex space-x-2 items-center">
              <span class="group-hover:underline truncate">gemma3n</span>
            </div>
          </h2>
          <p class="max-w-lg break-words text-neutral-800 text-md">Gemma 3n models are designed for efficient execution on everyday devices such as laptops, tablets or phones. </p>
        </div>
        <div class="flex flex-col space-y-2">
          <div class="flex flex-wrap space-x-2">
            <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">vision</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">e2b</span>
              <span  class="inline-flex items-center rounded-md bg-[#ddf4ff] px-2 py-0.5 text-xs font-medium text-blue-600 sm:text-[13px]">e4b</span>
          </div>
          <p class="flex space-x-5 text-[13px] font-medium text-neutral-500">
              <span class="flex items-center">
                <span >2.4M</span>
                <span class="hidden sm:flex">&nbsp;Pulls</span>
              </span>
              <span class="flex items-center">
                <span >11</span>
                <span class="hidden sm:flex">&nbsp;Tags</span>
              </span>
              <span class="flex items-center" title="Oct 14, 2026 5:10 PM UTC">
                <span class="hidden sm:flex">Updated&nbsp;</span>
                <span >1 week ago</span>
              </span>
          </p>
        </div>
      </a>
    </li>
<li  class="flex items-baseline border-b border-neutral-200 py-6">
      <a href="/library/glm-4.7-flash" class="group w-full space-y-5">
        <div  title="glm-4.7-flash" class="flex flex-col">
          <h2 class="truncate text-xl font-medium underline-offset-2 md:text-2xl">
            <div class="flex space-x-2 items-center">
              <span class="group-hover:underline truncate">glm-4.7-flash</span>
            </div>
          </h2>
          <p class="max-w-lg break-words text-neutral-800 text-md">As the strongest model in the 30B class, GLM-4.7-Flash offers a new option for lightweight deployment that balances performance and efficiency.</p>
        </div>
        <div class="flex flex-col space-y-2">
          <div class="flex flex-wrap space-x-2">
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">tools</span>
              <span  class="inline-flex items-center rounded-md bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-600 sm:text-[13px]">thinking</span>
          </div>
          <p class="flex space-x-5 text-[13px] font-medium text-neutral-500">
              <span class="flex items-center">
                <span >2.1M</span>
                <span class="hidden sm:flex">&nbsp;Pulls</span>
              </span>
              <span class="flex items-center">
                <span >4</span>
                <span class="hidden sm:flex">&nbsp;Tags</span>
              </span>
              <span class="flex items-center" title="Jun 5, 2026 4:16 PM UTC">
                <span class="hidden sm:flex">Updated&nbsp;</span>
                <span >4 months ago</span>
              </span>
          </p>
        </div>
      </a>
    </li>
""""";
}

/// <summary>Phase 119: a machine for the catalogue's class — a 24 GB card and 64 GB of RAM unless told otherwise.</summary>
internal static class CatalogMachine
{
    public static MachineFacts Of(long vramMiB = 24576, long ramMiB = 65536, long? vramLimit = null, bool ollama = true)
        => new(vramMiB > 0 ? [new GpuFacts(0, "card", vramMiB, vramMiB)] : [], ramMiB, ramMiB, 16,
            FitEngines.All.Select(e => new EngineFacts(e, e == FitEngines.Ollama && !ollama ? null : e, e != FitEngines.Ollama || ollama, null, "/m", 1 << 22, vramLimit, null)).ToArray(), []);
}
