import http = require("plugins/http");
import app = require("durandal/app");
import sys = require("durandal/system");
import router = require("plugins/router");

import collection = require("models/collection");
import database = require("models/database");
import document = require("models/document");
import deleteCollection = require("viewmodels/deleteCollection");
import raven = require("common/raven");
import pagedList = require("common/pagedList");

class status {

    displayName = "status";
    activeView = ko.observable('');
    data = ko.observable('');

    ravenDb: raven;

    constructor() {
        this.ravenDb = new raven();
    }

    activate(args) { 
        console.log(args);
        var view = args && args.view ? args.view : 'statistics';
        this.activeView(view);
    }
    canDeactivate() {
        return true; 
    } 

    switchView(view) {
        console.log(view);
        this.activeView(view);
        router.navigate("#status?view=" + view, false);

        //todo: switch to postbox
        if (view == "user-info") {
            this.ravenDb.userInfo()
                .done(info => {
                    //console.log(info);
                    this.data(JSON.stringify(info));
                });
        }
        
    }
}

export = status;