define(["require", "exports", "plugins/router", "models/collection", "models/database", "models/document", "viewmodels/deleteCollection", "common/raven", "common/pagedList"], function(require, exports, __router__, __collection__, __database__, __document__, __deleteCollection__, __raven__, __pagedList__) {
    
    
    
    var router = __router__;

    var collection = __collection__;
    var database = __database__;
    var document = __document__;
    var deleteCollection = __deleteCollection__;
    var raven = __raven__;
    var pagedList = __pagedList__;

    var status = (function () {
        function status() {
            this.displayName = "status";
            this.activeView = ko.observable('');
            this.data = ko.observable('');
            this.ravenDb = new raven();
        }
        status.prototype.activate = function (args) {
            console.log(args);
            var view = args && args.view ? args.view : 'statistics';
            this.activeView(view);
        };
        status.prototype.canDeactivate = function () {
            return true;
        };

        status.prototype.switchView = function (view) {
            var _this = this;
            console.log(view);
            this.activeView(view);
            router.navigate("#status?view=" + view, false);

            if (view == "user-info") {
                this.ravenDb.userInfo().done(function (info) {
                    console.log(info);
                    console.log(JSON.stringify(info));
                    _this.data(JSON.stringify(info));
                });
            }
        };
        return status;
    })();

    
    return status;
});
//# sourceMappingURL=status.js.map
