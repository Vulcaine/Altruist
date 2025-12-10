import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
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
export class SceneViewComponent implements OnInit {
  worlds: WorldSummary[] = [];
  selectedWorld: WorldSummary | null = null;

  partitions: PartitionUI[] = [];
  selectedPartition: PartitionUI | null = null;

  selectedObject: WorldObjectDto | null = null;
  lastWorldUpdate: Date | null = null;

  isLoading = false;
  isLoadingWorlds = false;
  hasData = false;
  worldCollapsed = false;

  autoUpdate = false;

  cameraInfo: CameraInfo | null = null;

  constructor(private readonly worldService: WorldDashboardService) {}

  ngOnInit(): void {
    this.loadWorlds();
  }

  private loadWorlds(): void {
    this.isLoadingWorlds = true;
    this.worldService.getWorlds().subscribe({
      next: (worlds) => {
        this.worlds = worlds;
        this.isLoadingWorlds = false;

        if (!this.selectedWorld && worlds.length > 0) {
          this.onSelectWorld(worlds[0]);
        }
      },
      error: () => {
        this.isLoadingWorlds = false;
      },
    });
  }

  onRefresh(): void {
    if (this.selectedWorld) {
      this.onSelectWorld(this.selectedWorld);
    } else {
      this.loadWorlds();
    }
  }

  onSelectWorld(world: WorldSummary): void {
    this.selectedWorld = world;
    this.selectedPartition = null;
    this.partitions = [];
    this.selectedObject = null;
    this.lastWorldUpdate = null;
    this.worldCollapsed = false;
    this.isLoading = true;
    this.hasData = false;

    this.worldService.streamWorldObjects(world.index).subscribe({
      next: (p) => {
        // partitions start collapsed by default
        this.partitions.push({ ...p, collapsed: true });
        this.hasData = true;
        this.isLoading = false;
      },
      error: () => {
        this.partitions = [];
        this.selectedPartition = null;
        this.selectedObject = null;
        this.isLoading = false;
        this.hasData = false;
      },
      complete: () => {
        this.isLoading = false;
      },
    });
  }

  get visibleObjects(): WorldObjectDto[] {
    if (this.selectedPartition) {
      return this.selectedPartition.objects;
    }
    return this.partitions.flatMap((p) => p.objects);
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
    if (this.isLoading) return 'Loading world...';
    if (!this.selectedWorld) return 'Select a world';
    if (!this.hasData) return 'No objects in this world';

    if (this.lastWorldUpdate) {
      const time = this.lastWorldUpdate.toLocaleTimeString();
      return `Live world data · Last update: ${time}`;
    }

    return 'Live world data';
  }
}
