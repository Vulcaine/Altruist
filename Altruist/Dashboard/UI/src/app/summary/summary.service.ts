// src/app/summary-view/summary.service.ts

import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { AltruistSummaryDto } from './summary.model';

@Injectable({ providedIn: 'root' })
export class SummaryDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/summary';

  getSummary(): Observable<AltruistSummaryDto> {
    return this.http.get<AltruistSummaryDto>(this.baseUrl);
  }
}
