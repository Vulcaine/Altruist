import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';

import { AppRoutingModule } from './app-routing.module';
import { SceneModule } from './scene/scene-module';
import { SessionComponent } from './session/session.component';

@NgModule({
  imports: [BrowserModule, AppRoutingModule, SceneModule, SessionComponent],
  providers: [],
})
export class AppModule {}
