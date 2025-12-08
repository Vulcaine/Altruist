import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { GlobeSceneComponent } from '../globe-scene/globe-scene.component';
import { WorldDashboardService } from '../services/world-dashboard.service';
import {
  WorldObjectDto,
  WorldSummary,
} from '../world-scene/models/world.model';
import { WorldSceneComponent } from '../world-scene/world-scene.component';

@Component({
  selector: 'app-scene-view',
  standalone: true,
  imports: [CommonModule, GlobeSceneComponent, WorldSceneComponent],
  templateUrl: './scene-view.component.html',
  styleUrl: './scene-view.component.scss',
})
export class SceneViewComponent implements OnInit {
  isLoading = false;
  isLoadingWorlds = false;
  hasData = false;

  worlds: WorldSummary[] = [];
  selectedWorld: WorldSummary | null = null;
  selectedWorldObjects: WorldObjectDto[] = [];
  selectedObject: WorldObjectDto | null = null;

  autoUpdate = false; // future use

  /** Last live update timestamp coming from WorldSceneComponent */
  lastWorldUpdate: Date | null = null;

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

        if (this.worlds.length > 0 && !this.selectedWorld) {
          this.onSelectWorld(this.worlds[0]);
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
    this.isLoading = true;
    this.selectedObject = null;
    this.lastWorldUpdate = null; // reset when changing world

    this.worldService.getWorldObjects(world.index).subscribe({
      next: (objects) => {
        this.selectedWorldObjects = objects;
        this.isLoading = false;
        this.hasData = objects.length > 0;

        // optionally auto-focus first object
        this.selectedObject = objects[0] ?? null;
      },
      error: () => {
        this.selectedWorldObjects = [];
        this.selectedObject = null;
        this.isLoading = false;
        this.hasData = false;
      },
    });
  }

  onSelectObject(obj: WorldObjectDto): void {
    this.selectedObject = obj;
  }

  /** Handler for the child's lastUpdateChanged event */
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
