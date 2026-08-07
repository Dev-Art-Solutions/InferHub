namespace InferHub.Node.Tools;

/// <summary>
/// Whether a model may be loaded onto this box's card right now (phase 48, D2). A pure function,
/// deliberately: this is the piece whose off-by-one costs somebody an out-of-memory error at 2am,
/// and a pure function is the piece a test can pin exhaustively.
/// </summary>
/// <remarks>
/// <para>
/// <b>The budget is declared, not detected</b> (D1). A node cannot reliably measure the card it is
/// on: under WSL2 there are no <c>/dev/nvidia*</c> device nodes, the host's <c>nvidia-smi</c> cannot
/// see the VM's VRAM, and the only reliable signal that a GPU exists at all is that
/// <c>libcuda.so.1</c> loads (phase-39 D5). So <c>Node:Vram:BudgetMiB</c> is a number the operator
/// sets and the worker's own reading is a cross-check that gets logged, never an override.
/// </para>
/// <para>
/// <b>It is an admission gate, not a scheduler.</b> Nothing here bin-packs, evicts a model somebody
/// else is mid-job on, or decides which node a request goes to. It answers one question about one
/// box — the same line phase 43 draws between desired state and orchestration.
/// </para>
/// <para>
/// Same shape as <c>NodeProfileClamp</c> (phase-43 D1): everything it needs arrives in its
/// arguments, so the adversarial suite is a table of inputs rather than a host.
/// </para>
/// </remarks>
internal static class VramBudget
{
    /// <summary>
    /// <c>Admit</c> — load it now. <c>Wait</c> — it fits on this box but not beside what is running,
    /// so the caller queues on the existing tool budget and then gets the usual <c>503</c> +
    /// <c>Retry-After</c>. <c>Refuse</c> — it cannot fit here at all, which is also why it is never
    /// declared.
    /// </summary>
    public enum Admission
    {
        Admit,
        Wait,
        Refuse
    }

    public readonly record struct Decision(Admission Outcome, string? Reason)
    {
        public static readonly Decision Admitted = new(Admission.Admit, null);

        public bool IsAdmitted => Outcome == Admission.Admit;
    }

    /// <summary>
    /// A model that is currently on the card, and whether it can be freed. <see cref="InUse"/> means
    /// a request is running against it: the swap frees <em>idle</em> pipelines, and a model somebody
    /// is mid-job on is not one of them.
    /// </summary>
    public readonly record struct Resident(string Model, int VramMiB, bool InUse);

    /// <summary>
    /// Evaluates one candidate against the declared budget.
    /// </summary>
    /// <param name="budgetMiB">
    /// <c>Node:Vram:BudgetMiB</c>. <b>Zero or less means unset</b>, which is today's behaviour
    /// exactly: everything is admitted and this function decides nothing. A deployment that changes
    /// no config behaves identically to v3.15.
    /// </param>
    /// <param name="reserveMiB">
    /// <c>Node:Vram:ReserveMiB</c> — held back for the inference backend and the display. With
    /// <c>maxWorkers: 1</c> the common case is one recipe loaded at a time, so the gate is really
    /// about the <em>second</em> thing on the card: an Ollama beside the diffusion container.
    /// </param>
    public static Decision Evaluate(
        int budgetMiB,
        int reserveMiB,
        IReadOnlyCollection<Resident> residents,
        string candidate,
        int candidateMiB)
    {
        if (budgetMiB <= 0)
        {
            return Decision.Admitted;
        }

        // A recipe already on the card costs nothing to use, and refusing it because the *budget*
        // moved under it would fail a request against weights that are sitting right there.
        if (residents.Any(r => string.Equals(r.Model, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return Decision.Admitted;
        }

        var headroom = budgetMiB - Math.Max(0, reserveMiB);

        if (headroom <= 0)
        {
            return new Decision(
                Admission.Refuse,
                $"Node:Vram:ReserveMiB ({reserveMiB}) leaves nothing of Node:Vram:BudgetMiB ({budgetMiB}) for a model");
        }

        // A recipe with no declared figure is admitted rather than guessed at. Inventing a number
        // for it would put a model the operator can see on the box behind a refusal derived from
        // arithmetic nobody wrote down.
        if (candidateMiB <= 0)
        {
            return Decision.Admitted;
        }

        if (candidateMiB > headroom)
        {
            return new Decision(
                Admission.Refuse,
                $"'{candidate}' needs {candidateMiB} MiB and this node budgets {headroom} MiB for models "
                + $"(Node:Vram:BudgetMiB {budgetMiB} minus Node:Vram:ReserveMiB {reserveMiB})");
        }

        // Only what is IN USE counts against the candidate. An idle pipeline is freed by the worker
        // before the next allocation (D3) — and freed *before* rather than after, so the peak is
        // never both models at once.
        var held = residents.Where(r => r.InUse).Sum(r => (long)r.VramMiB);

        if (held + candidateMiB <= headroom)
        {
            return Decision.Admitted;
        }

        return new Decision(
            Admission.Wait,
            $"'{candidate}' needs {candidateMiB} MiB, {held} MiB is held by work in flight, and this node "
            + $"budgets {headroom} MiB for models");
    }

    /// <summary>
    /// Whether a recipe should be declared at all. A model this box can never hold is not advertised,
    /// so the router never sends it work — phase-41 D6's withdraw-on-failure, applied <em>before</em>
    /// the first failure rather than after it.
    /// </summary>
    public static bool Fits(int budgetMiB, int reserveMiB, int recipeMiB) =>
        budgetMiB <= 0 || recipeMiB <= 0 || recipeMiB <= budgetMiB - Math.Max(0, reserveMiB);
}

/// <summary>
/// What this node believes is on the card, and which of it is busy (phase 48).
/// </summary>
/// <remarks>
/// <para>
/// <b>It mirrors the worker's own policy rather than measuring anything.</b> The worker holds at
/// most <c>Tools:Image:ResidentRecipes</c> pipelines and frees the least recently used one
/// <em>before</em> allocating the next, so the node applies the same rule at the same moment and the
/// two agree without a round trip. Where they can differ is a load that fails: the node then
/// believes the new model is resident when in fact nothing is, which errs toward <em>refusing</em>
/// work rather than toward an out-of-memory error. That asymmetry is the right one.
/// </para>
/// <para>
/// <b>In-use is what the gate actually reads.</b> A model somebody is mid-job on cannot be freed,
/// and phase-48 D2 says so out loud: the budget waits, it never evicts under a running request.
/// </para>
/// </remarks>
internal sealed class ImageResidency(int residentLimit)
{
    private readonly object gate = new();

    // Insertion-ordered, so the first key is the least recently used — the same structure the
    // worker keeps, for the same reason, and without a second one to keep in step.
    private readonly Dictionary<string, Entry> resident = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(int VramMiB)
    {
        public int InUse { get; set; }
    }

    public IReadOnlyCollection<VramBudget.Resident> Snapshot()
    {
        lock (gate)
        {
            return resident
                .Select(pair => new VramBudget.Resident(pair.Key, pair.Value.VramMiB, pair.Value.InUse > 0))
                .ToArray();
        }
    }

    /// <summary>Marks a model as loaded and busy, evicting idle ones down to the limit.</summary>
    public void Reserve(string model, int vramMiB)
    {
        lock (gate)
        {
            if (resident.TryGetValue(model, out var existing))
            {
                existing.InUse++;

                // Touch it: re-inserting is what moves it to the end of the insertion order, which
                // is what makes "the first key" mean "least recently used".
                resident.Remove(model);
                resident[model] = existing;
                return;
            }

            foreach (var victim in resident
                         .Where(pair => pair.Value.InUse == 0)
                         .Select(pair => pair.Key)
                         .Take(Math.Max(0, resident.Count - Math.Max(1, residentLimit) + 1))
                         .ToArray())
            {
                resident.Remove(victim);
            }

            resident[model] = new Entry(vramMiB) { InUse = 1 };
        }
    }

    /// <summary>The request finished. The weights stay; only the busy flag drops.</summary>
    public void Release(string model)
    {
        lock (gate)
        {
            if (resident.TryGetValue(model, out var entry) && entry.InUse > 0)
            {
                entry.InUse--;
            }
        }
    }

    /// <summary>
    /// The worker freed everything — an idle hint, a suspension, or a worker that went away. What
    /// is in flight is left alone: a lease still holds it, and forgetting that would let the gate
    /// admit a second model onto a card that is already busy with the first.
    /// </summary>
    public void Clear()
    {
        lock (gate)
        {
            foreach (var idle in resident.Where(pair => pair.Value.InUse == 0).Select(pair => pair.Key).ToArray())
            {
                resident.Remove(idle);
            }
        }
    }
}
