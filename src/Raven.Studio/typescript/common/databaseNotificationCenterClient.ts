/// <reference path="../../typings/tsd.d.ts" />

import database = require("models/resources/database");
import changeSubscription = require("common/changeSubscription");
import changesCallback = require("common/changesCallback");
import EVENTS = require("common/constants/events");
import endpoints = require("endpoints");

import abstractNotificationCenterClient = require("common/abstractNotificationCenterClient");

class databaseNotificationCenterClient extends abstractNotificationCenterClient {

    constructor(db: database) {
        super(db);
    }

    protected allDatabaseStatsChangedHandlers = ko.observableArray<changesCallback<Raven.Server.NotificationCenter.Notifications.DatabaseStatsChanged>>();

    get connectionDescription() {
        return "Notification Center Client: " + this.db.name;
    }

    protected onMessage(actionDto: Raven.Server.NotificationCenter.Notifications.Notification) {
        const actionType = actionDto.Type;

        switch (actionType) {
            case "DatabaseStatsChanged":
                const statsChangedDto = actionDto as Raven.Server.NotificationCenter.Notifications.DatabaseStatsChanged;
                this.fireEvents<Raven.Server.NotificationCenter.Notifications.DatabaseStatsChanged>(this.allDatabaseStatsChangedHandlers(), statsChangedDto, () => true);
                break;

            default:
                super.onMessage(actionDto);
        }
    }

    protected webSocketUrlFactory(token: singleAuthToken) {
        const connectionString = "?singleUseAuthToken=" + token.Token;
        return endpoints.databases.databaseNotificationCenter.notificationCenterWatch + connectionString;
    }

    watchAllDatabaseStatsChanged(onChange: (e: Raven.Server.NotificationCenter.Notifications.DatabaseStatsChanged) => void) {
        const callback = new changesCallback<Raven.Server.NotificationCenter.Notifications.DatabaseStatsChanged>(onChange);

        this.allDatabaseStatsChangedHandlers.push(callback);

        return new changeSubscription(() => {
            this.allDatabaseStatsChangedHandlers.remove(callback);
        });
    }

}

export = databaseNotificationCenterClient;

