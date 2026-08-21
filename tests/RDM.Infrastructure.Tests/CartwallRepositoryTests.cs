using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class CartwallRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly CartwallRepository _sut;
    private readonly List<string> _cartwallIds = new();

    public CartwallRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new CartwallRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_cartwallIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        // cart_slots cascade-delete with cartwall
        await conn.ExecuteAsync(
            "DELETE FROM cartwalls WHERE cartwall_id IN @Ids", new { Ids = _cartwallIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id = NewId();
        var cartwall = BuildCartwall(id, "Page 1");

        await _sut.CreateAsync(cartwall);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.CartwallId.Should().Be(id);
        result.Name.Should().Be("Page 1");
        result.StudioId.Should().Be(_fixture.StudioId);
        result.PageOrder.Should().Be(0);
    }

    [Fact]
    public async Task GetById_ShouldReturn()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildCartwall(id, "Effects", pageOrder: 2));

        var result = await _sut.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Effects");
        result.PageOrder.Should().Be(2);
    }

    [Fact]
    public async Task GetById_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid().ToString());
        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_ShouldModify()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildCartwall(id, "Old Name", pageOrder: 0));

        var updated = BuildCartwall(id, "New Name", pageOrder: 1);
        await _sut.UpdateAsync(updated);

        var result = await _sut.GetByIdAsync(id);
        result!.Name.Should().Be("New Name");
        result.PageOrder.Should().Be(1);
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnOrderedByPageOrder()
    {
        var id1 = NewId();
        var id2 = NewId();
        await _sut.CreateAsync(BuildCartwall(id1, "Second", pageOrder: 2));
        await _sut.CreateAsync(BuildCartwall(id2, "First",  pageOrder: 0));

        var result = await _sut.GetByStudioAsync(_fixture.StudioId);

        var ours = result.Where(c => c.CartwallId == id1 || c.CartwallId == id2).ToList();
        ours.Should().HaveCount(2);
        ours[0].CartwallId.Should().Be(id2);
        ours[1].CartwallId.Should().Be(id1);
    }

    [Fact]
    public async Task UpsertSlot_ShouldInsertNewSlot()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildCartwall(id, "With Slots"));

        var slot = BuildSlot(id, slotNumber: 1, label: "Jingle 1");
        await _sut.UpsertSlotAsync(slot);

        var slots = await _sut.GetSlotsAsync(id);
        slots.Should().HaveCount(1);
        slots[0].SlotNumber.Should().Be(1);
        slots[0].Label.Should().Be("Jingle 1");
        slots[0].Loop.Should().BeFalse();
        slots[0].OutputGainDb.Should().Be(0.0m);
    }

    [Fact]
    public async Task UpsertSlot_ShouldUpdateExistingSlot()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildCartwall(id, "Slots Upsert"));

        var original = BuildSlot(id, slotNumber: 1, label: "Original");
        await _sut.UpsertSlotAsync(original);

        var updated = BuildSlot(id, slotNumber: 1, label: "Updated", loop: true, gainDb: -3.0m);
        await _sut.UpsertSlotAsync(updated);

        var slots = await _sut.GetSlotsAsync(id);
        slots.Should().HaveCount(1);
        slots[0].Label.Should().Be("Updated");
        slots[0].Loop.Should().BeTrue();
        slots[0].OutputGainDb.Should().Be(-3.0m);
    }

    [Fact]
    public async Task GetSlots_ShouldReturnOrderedBySlotNumber()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildCartwall(id, "Ordered Slots"));
        await _sut.UpsertSlotAsync(BuildSlot(id, slotNumber: 3, label: "C"));
        await _sut.UpsertSlotAsync(BuildSlot(id, slotNumber: 1, label: "A"));
        await _sut.UpsertSlotAsync(BuildSlot(id, slotNumber: 2, label: "B"));

        var slots = await _sut.GetSlotsAsync(id);

        slots.Should().HaveCount(3);
        slots[0].SlotNumber.Should().Be(1);
        slots[1].SlotNumber.Should().Be(2);
        slots[2].SlotNumber.Should().Be(3);
    }

    [Fact]
    public async Task Delete_ShouldCascadeSlots()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildCartwall(id, "To Delete"));
        await _sut.UpsertSlotAsync(BuildSlot(id, slotNumber: 1));

        await _sut.DeleteAsync(id);
        _cartwallIds.Remove(id);

        var result = await _sut.GetByIdAsync(id);
        result.Should().BeNull();

        var slots = await _sut.GetSlotsAsync(id);
        slots.Should().BeEmpty();
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _cartwallIds.Add(id);
        return id;
    }

    private Cartwall BuildCartwall(string id, string name, byte pageOrder = 0) => new()
    {
        CartwallId = id,
        StudioId   = _fixture.StudioId,
        Name       = name,
        PageOrder  = pageOrder
    };

    private static CartSlot BuildSlot(
        string  cartwallId,
        byte    slotNumber,
        string? label   = null,
        bool    loop    = false,
        decimal gainDb  = 0.0m) => new()
    {
        SlotId       = Guid.NewGuid().ToString(),
        CartwallId   = cartwallId,
        SlotNumber   = slotNumber,
        Label        = label,
        Loop         = loop,
        OutputGainDb = gainDb
    };
}
