import http = require("plugins/http");
import app = require("durandal/app");
import sys = require("durandal/system");
import router = require("plugins/router");


import raven = require("common/raven");

class status {

    displayName = "status";
    activeView = ko.observable('');

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
    }
}

export = status;