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
      },
      error: (err) => {
        console.error(err);
        this.error = 'Failed to load Altruist summary.';
        this.isLoading = false;
      },
    });
  }

  get engine(): EngineInfoDto | null {
    return this.summary?.engine ?? null;
  }

  // ---------- configuration ----------

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

  // ---------- services & portals ----------

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

  // ---------- stats ----------

  get serviceCount(): number {
    return this.summary?.serviceCount ?? 0;
  }

  get configCount(): number {
    return this.summary?.configs.length ?? 0;
  }
}
