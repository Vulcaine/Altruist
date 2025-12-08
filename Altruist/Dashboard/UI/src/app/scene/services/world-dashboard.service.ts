import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  WorldObjectDto,
  WorldSummary,
} from '../world-scene/models/world.model';

@Injectable({
  providedIn: 'root',
})
export class WorldDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/worlds';

  getWorlds(): Observable<WorldSummary[]> {
    return this.http.get<WorldSummary[]>(this.baseUrl);
  }

  getWorldObjects(worldIndex: number): Observable<WorldObjectDto[]> {
    return this.http.get<WorldObjectDto[]>(
      `${this.baseUrl}/${worldIndex}/objects`
    );
  }
}
