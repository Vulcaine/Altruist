import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subscription, interval } from 'rxjs';
import { startWith } from 'rxjs/operators';
import { GlobeSceneComponent } from '../globe-scene/globe-scene.component';
import { WorldDashboardService } from '../services/world-dashboard.service';
import {
  WorldObjectDto,
  WorldPartitionDto,
  WorldSummary,
} from '../world-scene/models/world.model';
import { CameraInfo } from '../world-scene/world-renderer';
import { WorldSceneComponent } from '../world-scene/world-scene.component';

interface PartitionUI extends WorldPartitionDto {
  collapsed: boolean;
}

@Component({
  selector: 'app-scene-view',
  standalone: true,
  imports: [CommonModule, GlobeSceneComponent, WorldSceneComponent],
  templateUrl: './scene-view.component.html',
  styleUrl: './scene-view.component.scss',
})
export class SceneViewComponent implements OnInit, OnDestroy {
  worlds: WorldSummary[] = [];
  selectedWorld: WorldSummary | null = null;

  partitions: PartitionUI[] = [];
  selectedPartition: PartitionUI | null = null;

  selectedObject: WorldObjectDto | null = null;
  lastWorldUpdate: Date | null = null;

  isInitialLoading = false;

  isLoadingWorlds = false;
  hasData = false;
  worldCollapsed = false;

  autoUpdate = false;

  cameraInfo: CameraInfo | null = null;

  private autoUpdateSub?: Subscription;

  private isSnapshotInFlight = false;

  constructor(private readonly worldService: WorldDashboardService) {}

  ngOnInit(): void {
    this.loadWorlds();
  }

  ngOnDestroy(): void {
    this.stopAutoUpdate();
  }

  private loadWorlds(): void {
    this.isLoadingWorlds = true;

    this.worldService.getWorlds().subscribe({
      next: (worlds) => {
        this.worlds = worlds;
        this.isLoadingWorlds = false;

        if (this.selectedWorld) {
          const stillExists = worlds.some(
            (w) => w.index === this.selectedWorld!.index,
          );
          if (!stillExists) {
            this.selectedWorld = null;
            this.partitions = [];
            this.selectedPartition = null;
            this.selectedObject = null;
            this.hasData = false;
          }
        }

        if (!this.selectedWorld && worlds.length > 0) {
          this.onSelectWorld(worlds[0]);
        }
      },
      error: () => {
        this.isLoadingWorlds = false;
      },
    });
  }

  onSelectWorld(world: WorldSummary): void {
    this.selectedWorld = world;
    this.worldCollapsed = false;

    this.selectedPartition = null;
    this.selectedObject = null;
    this.partitions = [];
    this.hasData = false;
    this.lastWorldUpdate = null;

    this.isInitialLoading = true;

    const wasAuto = this.autoUpdate;
    this.stopAutoUpdate();

    this.refreshSnapshot(false, () => {
      if (wasAuto) {
        this.autoUpdate = true;
        this.startAutoUpdate();
      }
    });
  }

  onRefresh(): void {
    if (!this.selectedWorld) {
      this.loadWorlds();
      return;
    }

    this.refreshSnapshot(true);
  }

  private refreshSnapshot(
    preserveSelection: boolean,
    onDone?: () => void,
  ): void {
    if (!this.selectedWorld) return;

    if (this.isSnapshotInFlight) {
      onDone?.();
      return;
    }

    this.isSnapshotInFlight = true;

    const worldIndex = this.selectedWorld.index;

    const prevSelectedPartitionKey =
      preserveSelection && this.selectedPartition
        ? `${this.selectedPartition.indexX}:${this.selectedPartition.indexY}:${this.selectedPartition.indexZ}`
        : null;

    const prevSelectedObjectId =
      preserveSelection && this.selectedObject
        ? this.selectedObject.instanceId
        : null;

    this.worldService.getWorldObjectsSnapshot(worldIndex).subscribe({
      next: (snapshot) => {
        const incoming: WorldPartitionDto[] = snapshot.partitions ?? [];

        this.mergePartitions(incoming);

        this.hasData = this.partitions.length > 0;
        this.lastWorldUpdate = new Date();

        this.isInitialLoading = false;

        if (prevSelectedPartitionKey) {
          const foundPartition = this.partitions.find(
            (p) =>
              `${p.indexX}:${p.indexY}:${p.indexZ}` ===
              prevSelectedPartitionKey,
          );

          this.selectedPartition = foundPartition ?? null;

          if (foundPartition) {
            foundPartition.collapsed = false;
          }
        } else {
          if (this.selectedPartition) {
            const key = `${this.selectedPartition.indexX}:${this.selectedPartition.indexY}:${this.selectedPartition.indexZ}`;
            const stillExists = this.partitions.some(
              (p) => `${p.indexX}:${p.indexY}:${p.indexZ}` === key,
            );
            if (!stillExists) this.selectedPartition = null;
          }
        }

        if (prevSelectedObjectId) {
          const allObjects = this.partitions.flatMap((p) => p.objects ?? []);
          const foundObj = allObjects.find(
            (o) => o.instanceId === prevSelectedObjectId,
          );
          this.selectedObject = foundObj ?? null;
        } else {
          if (this.selectedObject) {
            const allObjects = this.partitions.flatMap((p) => p.objects ?? []);
            const stillExists = allObjects.some(
              (o) => o.instanceId === this.selectedObject!.instanceId,
            );
            if (!stillExists) this.selectedObject = null;
          }
        }

        if (!this.selectedPartition && this.partitions.length > 0) {
          this.selectedPartition = this.partitions[0];
        }

        this.isSnapshotInFlight = false;
        onDone?.();
      },
      error: () => {
        this.isInitialLoading = false;
        this.isSnapshotInFlight = false;
        onDone?.();
      },
    });
  }

  private mergePartitions(incoming: WorldPartitionDto[]): void {
    const existingPartitionMap = new Map<string, PartitionUI>();
    for (const p of this.partitions) {
      existingPartitionMap.set(`${p.indexX}:${p.indexY}:${p.indexZ}`, p);
    }

    const nextPartitions: PartitionUI[] = [];

    for (const incomingPartition of incoming) {
      const key = `${incomingPartition.indexX}:${incomingPartition.indexY}:${incomingPartition.indexZ}`;
      const existing = existingPartitionMap.get(key);

      if (!existing) {
        nextPartitions.push({
          ...incomingPartition,
          collapsed: true,
        });
        continue;
      }

      this.mergeObjects(existing, incomingPartition);
      nextPartitions.push(existing);
    }

    this.partitions = nextPartitions;
  }

  private mergeObjects(
    existingPartition: PartitionUI,
    incomingPartition: WorldPartitionDto,
  ): void {
    const existing = existingPartition.objects ?? [];
    const incoming = incomingPartition.objects ?? [];

    const byId = new Map<string, WorldObjectDto>();
    for (const obj of existing) {
      byId.set(obj.instanceId, obj);
    }

    const next: WorldObjectDto[] = [];

    for (const inObj of incoming) {
      const ex = byId.get(inObj.instanceId);

      if (!ex) {
        next.push(inObj);
        continue;
      }

      ex.archetype = inObj.archetype;
      ex.zoneId = inObj.zoneId;
      ex.clientId = inObj.clientId;
      ex.expired = inObj.expired;
      ex.transform = inObj.transform;
      ex.colliders = inObj.colliders;

      next.push(ex);
    }

    existingPartition.objects = next;
  }

  toggleAutoUpdate(): void {
    this.autoUpdate = !this.autoUpdate;

    if (this.autoUpdate) {
      this.startAutoUpdate();
    } else {
      this.stopAutoUpdate();
    }
  }

  private startAutoUpdate(): void {
    this.stopAutoUpdate();

    this.autoUpdateSub = interval(1000)
      .pipe(startWith(0))
      .subscribe(() => {
        if (!this.selectedWorld) return;
        this.refreshSnapshot(true);
      });
  }

  private stopAutoUpdate(): void {
    if (this.autoUpdateSub) {
      this.autoUpdateSub.unsubscribe();
      this.autoUpdateSub = undefined;
    }
  }

  get visibleObjects(): WorldObjectDto[] {
    if (this.selectedPartition) {
      return this.selectedPartition.objects ?? [];
    }
    return this.partitions.flatMap((p) => p.objects ?? []);
  }

  onSelectPartition(p: PartitionUI): void {
    this.selectedPartition = p;
    this.selectedObject = null;
  }

  onSelectObject(obj: WorldObjectDto): void {
    this.selectedObject = obj;
  }

  toggleWorld(): void {
    this.worldCollapsed = !this.worldCollapsed;
  }

  togglePartition(p: PartitionUI, e: MouseEvent): void {
    e.stopPropagation();
    p.collapsed = !p.collapsed;
  }

  onCameraChanged(info: CameraInfo): void {
    this.cameraInfo = info;
  }

  onWorldLastUpdate(ts: Date): void {
    this.lastWorldUpdate = ts;
  }

  get statusHint(): string {
    if (!this.selectedWorld) return 'Select a world';

    if (this.isInitialLoading && !this.hasData) return 'Loading world...';

    if (!this.hasData) return 'No objects in this world';

    if (this.autoUpdate) {
      return 'Auto-update ON · refreshing every 1s';
    }

    if (this.lastWorldUpdate) {
      const time = this.lastWorldUpdate.toLocaleTimeString();
      return `Live world data · Last update: ${time}`;
    }

    return 'Live world data';
  }
}
