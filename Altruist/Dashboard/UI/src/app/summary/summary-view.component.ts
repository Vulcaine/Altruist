import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AltruistSummaryDto,
  ConfigEntryDto,
  EngineInfoDto,
  ServiceCategory,
  ServiceInfoDto,
} from './summary.model';
import { SummaryDashboardService } from './summary.service';

@Component({
  selector: 'app-summary-view',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './summary-view.component.html',
  styleUrl: './summary-view.component.scss',
})
export class SummaryViewComponent implements OnInit {
  summary: AltruistSummaryDto | null = null;
  isLoading = false;
  error: string | null = null;

  configFilter = '';
  serviceFilter = '';

  readonly ServiceCategory = ServiceCategory;

  // Editing state
  editingKey: string | null = null;
  editBuffer: Record<string, string> = {}; // key → modified value

  constructor(private readonly summaryService: SummaryDashboardService) {}

  ngOnInit(): void {
    this.loadSummary();
  }

  loadSummary(): void {
    this.isLoading = true;
    this.error = null;

    this.summaryService.getSummary().subscribe({
      next: (s) => {
        this.summary = s;
        this.isLoading = false;
        this.editingKey = null;
        this.editBuffer = {};
      },
      error: (err) => {
        console.error(err);
        this.error = 'Failed to load Altruist summary.';
        this.isLoading = false;
      },
    });
  }

  // ---------------------------------------------------
  // CONFIG ENTRIES
  // ---------------------------------------------------

  get configEntries(): ConfigEntryDto[] {
    if (!this.summary) return [];
    const ft = this.configFilter.trim().toLowerCase();
    if (!ft) return this.summary.configs;

    return this.summary.configs.filter(
      (c) =>
        c.key.toLowerCase().includes(ft) ||
        (c.value ?? '').toLowerCase().includes(ft)
    );
  }

  beginEdit(key: string, currentValue: string | null) {
    const entry = this.summary?.configs.find((x) => x.key === key);
    if (!entry || !entry.modifiable) return;

    this.editingKey = key;
    this.editBuffer[key] = currentValue ?? '';
  }

  onEditInput(key: string, value: string) {
    this.editBuffer[key] = value;
  }

  cancelEdit() {
    this.editingKey = null;
  }

  get hasPendingConfigChanges(): boolean {
    return Object.keys(this.editBuffer).length > 0;
  }

  saveConfigChanges() {
    const updates = Object.entries(this.editBuffer).map(([key, value]) => ({
      key,
      value,
    }));

    this.summaryService.updateConfigBatch(updates).subscribe({
      next: () => {
        this.loadSummary(); // reload fresh config
      },
      error: () => {
        this.error = 'Failed to update configuration.';
      },
    });
  }

  // ---------------------------------------------------
  // Service filtering
  // ---------------------------------------------------

  get engine(): EngineInfoDto | null {
    return this.summary?.engine ?? null;
  }

  get services(): ServiceInfoDto[] {
    if (!this.summary) return [];
    const ft = this.serviceFilter.trim().toLowerCase();
    if (!ft) return this.summary.services;

    return this.summary.services.filter((s) => {
      return (
        s.name.toLowerCase().includes(ft) ||
        s.fullName.toLowerCase().includes(ft) ||
        s.assembly.toLowerCase().includes(ft) ||
        (s.endpoint ?? '').toLowerCase().includes(ft) ||
        (s.serviceType ?? '').toLowerCase().includes(ft)
      );
    });
  }

  get portals(): ServiceInfoDto[] {
    return this.services.filter((s) => s.category === ServiceCategory.Portal);
  }

  get altruistServices(): ServiceInfoDto[] {
    return this.services.filter((s) => s.category === ServiceCategory.Service);
  }

  get serviceFactories(): ServiceInfoDto[] {
    return this.services.filter(
      (s) => s.category === ServiceCategory.ServiceFactory
    );
  }

  get serviceConfigs(): ServiceInfoDto[] {
    return this.services.filter(
      (s) => s.category === ServiceCategory.ServiceConfiguration
    );
  }

  get serviceCount(): number {
    return this.summary?.serviceCount ?? 0;
  }

  get configCount(): number {
    return this.summary?.configs.length ?? 0;
  }
}
