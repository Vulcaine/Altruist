import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { GlobeSceneComponent } from '../globe-scene/globe-scene.component';
import { WorldObjectDto, WorldSummary } from '../models/world.model';
import { WorldDashboardService } from '../services/world-dashboard.service';
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
  hasData = false;

  worlds: WorldSummary[] = [];
  selectedWorld: WorldSummary | null = null;
  selectedWorldObjects: WorldObjectDto[] = [];

  autoUpdate = false; // future use

  constructor(private readonly worldService: WorldDashboardService) {}

  ngOnInit(): void {
    this.loadWorlds();
  }

  private loadWorlds(): void {
    this.isLoading = true;
    this.worldService.getWorlds().subscribe({
      next: (worlds) => {
        this.worlds = worlds;
        this.isLoading = false;
        this.hasData = this.worlds.length > 0;

        // Optionally auto-select first world
        if (this.worlds.length > 0) {
          this.onSelectWorld(this.worlds[0]);
        }
      },
      error: () => {
        this.isLoading = false;
        this.hasData = false;
      },
    });
  }

  onRefresh(): void {
    if (this.selectedWorld) {
      this.onSelectWorld(this.selectedWorld, { keepSelection: true });
    } else {
      this.loadWorlds();
    }
  }

  onSelectWorld(
    world: WorldSummary,
    options: { keepSelection?: boolean } = {}
  ): void {
    this.selectedWorld = world;
    this.isLoading = true;

    this.worldService.getWorldObjects(world.index).subscribe({
      next: (objects) => {
        this.selectedWorldObjects = objects;
        this.isLoading = false;
        this.hasData = true;
      },
      error: () => {
        this.selectedWorldObjects = [];
        this.isLoading = false;
        this.hasData = false;
      },
    });
  }

  get statusHint(): string {
    if (this.isLoading) return 'Loading...';
    if (!this.hasData) return 'No data available';
    return 'Live world data';
  }
}
